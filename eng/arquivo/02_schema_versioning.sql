\set ON_ERROR_STOP on
-- Permite varias versoes de esquema do mesmo conjunto, mantendo cada versao em tabela propria.
-- Migra as tabelas atuais sem renomear nem reescrever dados ja carregados.
BEGIN;

LOCK TABLE arquivo.conjunto, arquivo.publicacao, arquivo.particao IN ACCESS EXCLUSIVE MODE;

ALTER TABLE arquivo.publicacao DROP CONSTRAINT IF EXISTS publicacao_conjunto_id_fkey;
ALTER TABLE arquivo.publicacao DROP CONSTRAINT IF EXISTS publicacao_conjunto_id_definition_version_fkey;
ALTER TABLE arquivo.publicacao DROP CONSTRAINT IF EXISTS publicacao_conjunto_definition_fk;
ALTER TABLE arquivo.publicacao DROP CONSTRAINT IF EXISTS publicacao_publication_conjunto_unique;
ALTER TABLE arquivo.publicacao DROP CONSTRAINT IF EXISTS publicacao_publication_id_conjunto_id_key;

ALTER TABLE arquivo.particao DROP CONSTRAINT IF EXISTS particao_publication_id_fkey;
ALTER TABLE arquivo.particao DROP CONSTRAINT IF EXISTS particao_conjunto_id_fkey;
ALTER TABLE arquivo.particao DROP CONSTRAINT IF EXISTS particao_publication_id_conjunto_id_fkey;
ALTER TABLE arquivo.particao DROP CONSTRAINT IF EXISTS particao_publication_conjunto_fk;

ALTER TABLE arquivo.conjunto DROP CONSTRAINT IF EXISTS conjunto_pkey;
ALTER TABLE arquivo.conjunto
    ADD CONSTRAINT conjunto_pkey PRIMARY KEY (conjunto_id, definition_version);

DO $validate_existing$
BEGIN
    IF EXISTS (
        SELECT 1 FROM arquivo.publicacao p
        LEFT JOIN arquivo.conjunto c
          ON c.conjunto_id=p.conjunto_id AND c.definition_version=p.definition_version
        WHERE c.conjunto_id IS NULL
    ) THEN
        RAISE EXCEPTION 'Há publicações sem a versão correspondente em arquivo.conjunto; migração cancelada.';
    END IF;
    IF EXISTS (
        SELECT 1 FROM arquivo.particao p
        JOIN arquivo.publicacao pub ON pub.publication_id=p.publication_id
        WHERE p.conjunto_id <> pub.conjunto_id
    ) THEN
        RAISE EXCEPTION 'Há partições vinculadas a conjunto diferente da publicação; migração cancelada.';
    END IF;
END
$validate_existing$;

ALTER TABLE arquivo.publicacao
    ADD CONSTRAINT publicacao_publication_conjunto_unique UNIQUE (publication_id, conjunto_id);
ALTER TABLE arquivo.publicacao
    ADD CONSTRAINT publicacao_conjunto_definition_fk
    FOREIGN KEY (conjunto_id, definition_version)
    REFERENCES arquivo.conjunto(conjunto_id, definition_version);
ALTER TABLE arquivo.particao
    ADD CONSTRAINT particao_publication_conjunto_fk
    FOREIGN KEY (publication_id, conjunto_id)
    REFERENCES arquivo.publicacao(publication_id, conjunto_id);

CREATE OR REPLACE FUNCTION arquivo.registrar_conjunto(
    p_conjunto_id text,
    p_columns text[],
    p_definition_version integer DEFAULT 1
) RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, arquivo
AS $function$
DECLARE
    v_base_table text;
    v_table text;
    v_existing arquivo.conjunto%ROWTYPE;
    v_columns_sql text;
BEGIN
    IF p_conjunto_id IS NULL OR p_conjunto_id !~ '^[a-z0-9][a-z0-9._-]{0,127}$' THEN
        RAISE EXCEPTION 'Identificador de conjunto inválido: %', p_conjunto_id;
    END IF;
    IF p_definition_version IS NULL OR p_definition_version < 1 OR p_columns IS NULL OR cardinality(p_columns) = 0 THEN
        RAISE EXCEPTION 'Versão ou lista de colunas inválida para %', p_conjunto_id;
    END IF;
    IF EXISTS (
        SELECT 1 FROM unnest(p_columns) AS u(col)
        WHERE col !~ '^(?:[a-z][a-z0-9_]{0,62}|_[a-z][a-z0-9_]{0,61})$'
           OR col IN ('publicacao_id','particao_id','_linha')
    ) OR cardinality(p_columns) <> (SELECT count(DISTINCT col) FROM unnest(p_columns) AS u(col)) THEN
        RAISE EXCEPTION 'Colunas inválidas ou duplicadas para %', p_conjunto_id;
    END IF;

    v_base_table := trim(both '_' FROM regexp_replace(p_conjunto_id, '[^a-z0-9]+', '_', 'g'));
    IF v_base_table = '' OR v_base_table !~ '^[a-z]' THEN v_base_table := 'c_' || v_base_table; END IF;
    IF length(v_base_table) > 63 THEN
        v_base_table := left(v_base_table, 54) || '_' || substr(md5(p_conjunto_id), 1, 8);
    END IF;

    SELECT * INTO v_existing
    FROM arquivo.conjunto
    WHERE conjunto_id=p_conjunto_id AND definition_version=p_definition_version;
    IF FOUND THEN
        IF v_existing.columns <> p_columns THEN
            RAISE EXCEPTION 'O conjunto % versão % já existe com outras colunas.', p_conjunto_id, p_definition_version;
        END IF;
        RETURN v_existing.table_name;
    END IF;

    v_table := v_base_table;
    IF EXISTS (SELECT 1 FROM arquivo.conjunto WHERE conjunto_id=p_conjunto_id) THEN
        IF length(v_base_table) + length('_v' || p_definition_version::text) <= 63 THEN
            v_table := v_base_table || '_v' || p_definition_version::text;
        ELSE
            v_table := left(v_base_table, 54) || '_' || substr(md5(p_conjunto_id || ':' || p_definition_version::text), 1, 8);
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM arquivo.conjunto WHERE table_name=v_table) THEN
        RAISE EXCEPTION 'Colisão de nome de tabela: arquivo.%', v_table;
    END IF;

    SELECT string_agg(format('%I text', c), ', ' ORDER BY ord)
      INTO v_columns_sql
      FROM unnest(p_columns) WITH ORDINALITY AS u(c, ord);
    EXECUTE format(
        'CREATE TABLE arquivo.%I (publicacao_id text NOT NULL, particao_id text NOT NULL, _linha bigint NOT NULL, %s)',
        v_table, v_columns_sql);
    EXECUTE format('GRANT SELECT, INSERT ON arquivo.%I TO govdata_arquivo_loader', v_table);
    INSERT INTO arquivo.conjunto(conjunto_id, table_name, columns, definition_version)
    VALUES (p_conjunto_id, v_table, p_columns, p_definition_version);
    RETURN v_table;
END
$function$;

REVOKE ALL ON FUNCTION arquivo.registrar_conjunto(text, text[], integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION arquivo.registrar_conjunto(text, text[], integer) TO govdata_arquivo_loader;

COMMIT;

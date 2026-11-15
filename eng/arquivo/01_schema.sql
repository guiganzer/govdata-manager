-- PostgreSQL 16+ · executar como postgres no banco dados_publicos.
-- Cria o armazenamento de cargas por Parquet sem alterar schema raw ou endpoints de API.
BEGIN;

CREATE SCHEMA IF NOT EXISTS arquivo;

DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'govdata_arquivo_loader') THEN
        CREATE ROLE govdata_arquivo_loader NOLOGIN;
    END IF;
END
$role$;

CREATE TABLE IF NOT EXISTS arquivo.conjunto (
    conjunto_id          text NOT NULL,
    table_name           text NOT NULL UNIQUE,
    columns              text[] NOT NULL,
    definition_version   integer NOT NULL DEFAULT 1 CHECK (definition_version > 0),
    created_at           timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (conjunto_id, definition_version),
    CHECK (conjunto_id ~ '^[a-z0-9][a-z0-9._-]{0,127}$'),
    CHECK (table_name ~ '^[a-z][a-z0-9_]{0,62}$'),
    CHECK (cardinality(columns) > 0)
);

CREATE TABLE IF NOT EXISTS arquivo.publicacao (
    publication_id       text PRIMARY KEY,
    conjunto_id          text NOT NULL,
    definition_version   integer NOT NULL DEFAULT 1 CHECK (definition_version > 0),
    status               text NOT NULL DEFAULT 'PENDENTE'
                         CHECK (status IN ('PENDENTE','CARREGANDO','CARREGADA','ERRO')),
    priority             integer NOT NULL DEFAULT 0,
    created_at           timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at           timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (publication_id, conjunto_id),
    FOREIGN KEY (conjunto_id, definition_version)
        REFERENCES arquivo.conjunto(conjunto_id, definition_version),
    CHECK (publication_id ~ '^[a-f0-9]{64}$')
);

ALTER TABLE arquivo.publicacao ADD COLUMN IF NOT EXISTS definition_version integer NOT NULL DEFAULT 1;

DO $publication_version_constraint$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_constraint
        WHERE conrelid = 'arquivo.publicacao'::regclass AND conname = 'publicacao_definition_version_positive'
    ) THEN
        ALTER TABLE arquivo.publicacao ADD CONSTRAINT publicacao_definition_version_positive
            CHECK (definition_version > 0);
    END IF;
END
$publication_version_constraint$;

CREATE TABLE IF NOT EXISTS arquivo.particao (
    publication_id       text NOT NULL,
    conjunto_id          text NOT NULL,
    definition_version   integer NOT NULL DEFAULT 1 CHECK (definition_version > 0),
    partition_id         text NOT NULL,
    relative_path        text NOT NULL,
    expected_rows        bigint NOT NULL CHECK (expected_rows > 0),
    parquet_sha256       text,
    rows_loaded          bigint NOT NULL DEFAULT 0 CHECK (rows_loaded >= 0),
    last_line            bigint NOT NULL DEFAULT 0 CHECK (last_line >= 0),
    status               text NOT NULL DEFAULT 'PENDENTE'
                         CHECK (status IN ('PENDENTE','CARREGANDO','CARREGADA','ERRO')),
    attempts             integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    claim_token          uuid,
    lease_until          timestamptz,
    started_at           timestamptz,
    completed_at         timestamptz,
    last_error           text,
    created_at           timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at           timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (publication_id, partition_id),
    FOREIGN KEY (publication_id, conjunto_id)
        REFERENCES arquivo.publicacao(publication_id, conjunto_id),
    CHECK (partition_id <> '' AND length(partition_id) <= 256),
    CHECK (relative_path !~ '(^/|(^|/)\.\.(/|$)|\\)'),
    CHECK (parquet_sha256 IS NULL OR parquet_sha256 ~ '^[a-fA-F0-9]{64}$'),
    CHECK (rows_loaded = last_line AND rows_loaded <= expected_rows)
);

ALTER TABLE arquivo.particao ADD COLUMN IF NOT EXISTS claim_token uuid;
ALTER TABLE arquivo.particao ADD COLUMN IF NOT EXISTS source_sha256 text;

DO $constraint$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_constraint
        WHERE conrelid = 'arquivo.particao'::regclass AND conname = 'particao_source_sha256_format'
    ) THEN
        ALTER TABLE arquivo.particao ADD CONSTRAINT particao_source_sha256_format
            CHECK (source_sha256 IS NULL OR source_sha256 ~ '^[a-fA-F0-9]{64}$');
    END IF;
END
$constraint$;

CREATE INDEX IF NOT EXISTS ix_arquivo_particao_pendente
    ON arquivo.particao (status, created_at, publication_id)
    WHERE status IN ('PENDENTE','CARREGANDO');

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
    WHERE conjunto_id = p_conjunto_id AND definition_version = p_definition_version;
    IF FOUND THEN
        IF v_existing.columns <> p_columns THEN
            RAISE EXCEPTION 'O conjunto % já existe com outro esquema. Use uma versão de conjunto explícita.', p_conjunto_id;
        END IF;
        RETURN v_existing.table_name;
    END IF;
    v_table := v_base_table;
    IF EXISTS (SELECT 1 FROM arquivo.conjunto WHERE conjunto_id = p_conjunto_id) THEN
        IF length(v_base_table) + length('_v' || p_definition_version::text) <= 63 THEN
            v_table := v_base_table || '_v' || p_definition_version::text;
        ELSE
            v_table := left(v_base_table, 54) || '_' || substr(md5(p_conjunto_id || ':' || p_definition_version::text), 1, 8);
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM arquivo.conjunto WHERE table_name = v_table) THEN
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

GRANT USAGE ON SCHEMA arquivo TO govdata_arquivo_loader;
GRANT SELECT ON arquivo.conjunto TO govdata_arquivo_loader;
GRANT SELECT, INSERT, UPDATE ON arquivo.publicacao, arquivo.particao TO govdata_arquivo_loader;
REVOKE ALL ON FUNCTION arquivo.registrar_conjunto(text, text[], integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION arquivo.registrar_conjunto(text, text[], integer) TO govdata_arquivo_loader;

COMMIT;

-- Configure uma senha fora deste arquivo SQL e habilite o login apenas quando o fluxo for instalado:
--   sudo -u postgres psql -X -d dados_publicos -c "ALTER ROLE govdata_arquivo_loader LOGIN"
--   sudo -u postgres psql -X -d dados_publicos   (depois, dentro do psql, execute: \password govdata_arquivo_loader)
--   sudo -u postgres psql -X -d dados_publicos -c "GRANT CONNECT ON DATABASE dados_publicos TO govdata_arquivo_loader"

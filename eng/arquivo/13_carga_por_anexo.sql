\set ON_ERROR_STOP on
\pset pager off

-- Aplicar como postgres em dados_publicos, depois de 12_carga_por_particao.sql.
--
-- Carga por anexo de particao. Antes: COPY na tabela de preparo e, ao final,
-- INSERT INTO conjunto SELECT * FROM preparo (cada linha gravada duas vezes,
-- WAL em dobro e indice particao_id mantido linha a linha). Agora:
--   * conjuntos novos nascem como tabela PARTITION BY LIST (particao_id);
--   * o preparo recebe CHECK (particao_id = valor) e vira a particao com
--     ATTACH PARTITION, sem copiar dados nem varrer a tabela;
--   * a particao anterior do mesmo particao_id e desanexada e removida.
-- Tabelas comuns ja existentes continuam funcionando pelo caminho antigo
-- (DELETE + INSERT); nada e convertido nem apagado por este script.
BEGIN;
SET LOCAL lock_timeout = '10s';

DO $preflight$
BEGIN
    IF current_database() <> 'dados_publicos' THEN
        RAISE EXCEPTION 'Aplique em dados_publicos.';
    END IF;
    IF to_regprocedure('arquivo.preparar_particao(text,text,uuid)') IS NULL OR
       to_regprocedure('arquivo.finalizar_particao(text,text)') IS NULL THEN
        RAISE EXCEPTION 'Aplique antes 12_carga_por_particao.sql.';
    END IF;
END
$preflight$;

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
    -- Cada particao_id (arquivo de origem) vira uma particao fisica propria.
    EXECUTE format(
        'CREATE TABLE arquivo.%I (publicacao_id text NOT NULL, particao_id text NOT NULL, _linha bigint NOT NULL, %s) '
        'PARTITION BY LIST (particao_id)',
        v_table, v_columns_sql);
    EXECUTE format('GRANT SELECT ON arquivo.%I TO govdata_arquivo_loader', v_table);
    INSERT INTO arquivo.conjunto(conjunto_id, table_name, columns, definition_version)
    VALUES (p_conjunto_id, v_table, p_columns, p_definition_version);
    RETURN v_table;
END
$function$;

CREATE OR REPLACE FUNCTION arquivo.preparar_particao(
    p_publication_id text, p_partition_id text, p_claim_token uuid
) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, arquivo
AS $function$
DECLARE
    v_table text;
    v_stage text;
    v_rows bigint;
    v_max bigint;
    v_checkpoint bigint;
    v_partitioned boolean;
BEGIN
    SELECT c.table_name, p.last_line INTO v_table, v_checkpoint
    FROM arquivo.particao p
    JOIN arquivo.publicacao pub ON pub.publication_id=p.publication_id
    JOIN arquivo.conjunto c ON c.conjunto_id=pub.conjunto_id
                           AND c.definition_version=pub.definition_version
    WHERE p.publication_id=p_publication_id AND p.partition_id=p_partition_id
      AND p.status='CARREGANDO' AND p.claim_token=p_claim_token;
    IF NOT FOUND THEN RAISE EXCEPTION 'Claim da particao nao encontrado.'; END IF;
    SELECT relkind = 'p' INTO v_partitioned FROM pg_class WHERE oid = format('arquivo.%I', v_table)::regclass;
    IF NOT v_partitioned THEN
        -- Tabela comum antiga: o indice acelera o DELETE da troca final.
        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON arquivo.%I (particao_id)',
            'ix_part_' || substr(md5(v_table), 1, 24), v_table);
    END IF;
    v_stage := 'stage_' || substr(md5(p_publication_id || ':' || p_partition_id), 1, 32);
    EXECUTE format('CREATE TABLE IF NOT EXISTS arquivo.%I (LIKE arquivo.%I)', v_stage, v_table);
    EXECUTE format('GRANT SELECT, INSERT ON arquivo.%I TO govdata_arquivo_loader', v_stage);
    -- O CHECK prova ao PostgreSQL que o preparo so contem esta particao: o ATTACH
    -- final dispensa a varredura de validacao. Linhas de outra particao sao recusadas no COPY.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conrelid = format('arquivo.%I', v_stage)::regclass AND conname = 'ck_particao') THEN
        EXECUTE format('ALTER TABLE arquivo.%I ADD CONSTRAINT ck_particao CHECK (particao_id IS NOT NULL AND particao_id = %L)',
            v_stage, p_partition_id);
    END IF;
    EXECUTE format('SELECT count(*), coalesce(max(_linha),0) FROM arquivo.%I', v_stage)
      INTO v_rows, v_max;
    IF v_rows <> v_checkpoint OR v_max <> v_checkpoint THEN
        RAISE EXCEPTION 'Preparo % diverge do checkpoint: linhas %, max %, checkpoint %.',
            v_stage, v_rows, v_max, v_checkpoint;
    END IF;
    RETURN v_stage;
END
$function$;

CREATE OR REPLACE FUNCTION arquivo.finalizar_particao(
    p_publication_id text, p_partition_id text
) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, arquivo
AS $function$
DECLARE
    v_conjunto text;
    v_table text;
    v_stage text;
    v_expected bigint;
    v_stage_rows bigint;
    v_old_table text;
    v_old_partition text;
    v_final text;
    v_bound text := format('FOR VALUES IN (%L)', p_partition_id);
BEGIN
    SELECT pub.conjunto_id, c.table_name, p.expected_rows
      INTO v_conjunto, v_table, v_expected
    FROM arquivo.particao p
    JOIN arquivo.publicacao pub ON pub.publication_id=p.publication_id
    JOIN arquivo.conjunto c ON c.conjunto_id=pub.conjunto_id
                           AND c.definition_version=pub.definition_version
    WHERE p.publication_id=p_publication_id AND p.partition_id=p_partition_id
      AND p.status='CARREGADA' AND p.rows_loaded=p.expected_rows;
    IF NOT FOUND THEN RAISE EXCEPTION 'Particao nao esta completamente carregada.'; END IF;
    v_stage := 'stage_' || substr(md5(p_publication_id || ':' || p_partition_id), 1, 32);
    IF to_regclass(format('arquivo.%I', v_stage)) IS NULL THEN
        RAISE EXCEPTION 'Tabela de preparo % ausente.', v_stage;
    END IF;
    EXECUTE format('SELECT count(*) FROM arquivo.%I', v_stage) INTO v_stage_rows;
    IF v_stage_rows <> v_expected THEN
        RAISE EXCEPTION 'Preparo possui % linhas; esperadas %.', v_stage_rows, v_expected;
    END IF;
    -- Serializa a substituicao do mesmo conjunto/particao. Leitores veem o
    -- estado anterior ate o commit.
    PERFORM pg_advisory_xact_lock(hashtextextended(v_conjunto || ':' || p_partition_id, 0));

    -- Remove a carga anterior da mesma particao em todas as versoes do conjunto.
    FOR v_old_table IN SELECT table_name FROM arquivo.conjunto
                       WHERE conjunto_id=v_conjunto LOOP
        IF (SELECT relkind FROM pg_class WHERE oid = format('arquivo.%I', v_old_table)::regclass) = 'p' THEN
            FOR v_old_partition IN
                SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                WHERE i.inhparent = format('arquivo.%I', v_old_table)::regclass
                  AND pg_get_expr(c.relpartbound, c.oid) = v_bound
            LOOP
                EXECUTE format('ALTER TABLE arquivo.%I DETACH PARTITION arquivo.%I', v_old_table, v_old_partition);
                EXECUTE format('DROP TABLE arquivo.%I RESTRICT', v_old_partition);
            END LOOP;
        ELSE
            EXECUTE format('DELETE FROM arquivo.%I WHERE particao_id=$1', v_old_table)
              USING p_partition_id;
        END IF;
    END LOOP;

    IF (SELECT relkind FROM pg_class WHERE oid = format('arquivo.%I', v_table)::regclass) = 'p' THEN
        -- Nome estavel por tabela/particao; o preparo vira a particao sem copiar dados.
        v_final := 'p_' || substr(md5(v_table || ':' || p_partition_id), 1, 32);
        EXECUTE format('REVOKE INSERT ON arquivo.%I FROM govdata_arquivo_loader', v_stage);
        EXECUTE format('ALTER TABLE arquivo.%I RENAME TO %I', v_stage, v_final);
        EXECUTE format('ALTER TABLE arquivo.%I ATTACH PARTITION arquivo.%I %s', v_table, v_final, v_bound);
        -- Redundante apos o anexo: a restricao da propria particao garante o mesmo.
        EXECUTE format('ALTER TABLE arquivo.%I DROP CONSTRAINT ck_particao', v_final);
        EXECUTE format('ANALYZE arquivo.%I', v_final);
    ELSE
        EXECUTE format('INSERT INTO arquivo.%I SELECT * FROM arquivo.%I', v_table, v_stage);
        EXECUTE format('DROP TABLE arquivo.%I RESTRICT', v_stage);
    END IF;

    -- Sem historico de reuso: metadados antigos da mesma particao saem junto
    -- com as linhas antigas. A publicacao antiga so sai se ficar vazia.
    DELETE FROM arquivo.particao old
    USING arquivo.publicacao pub
    WHERE old.publication_id=pub.publication_id
      AND pub.conjunto_id=v_conjunto AND old.partition_id=p_partition_id
      AND old.publication_id<>p_publication_id AND old.status='CARREGADA';
    IF EXISTS (
        SELECT 1 FROM arquivo.particao old
        JOIN arquivo.publicacao pub USING (publication_id)
        WHERE pub.conjunto_id=v_conjunto AND old.partition_id=p_partition_id
          AND old.publication_id<>p_publication_id
    ) THEN
        RAISE EXCEPTION 'Existe outra carga nao concluida para a mesma particao.';
    END IF;
    DELETE FROM arquivo.publicacao pub
    WHERE pub.conjunto_id=v_conjunto AND pub.publication_id<>p_publication_id
      AND NOT EXISTS (SELECT 1 FROM arquivo.particao p WHERE p.publication_id=pub.publication_id);
END
$function$;

REVOKE ALL ON FUNCTION arquivo.registrar_conjunto(text, text[], integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION arquivo.preparar_particao(text,text,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION arquivo.finalizar_particao(text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION arquivo.registrar_conjunto(text, text[], integer) TO govdata_arquivo_loader;
GRANT EXECUTE ON FUNCTION arquivo.preparar_particao(text,text,uuid) TO govdata_arquivo_loader;
GRANT EXECUTE ON FUNCTION arquivo.finalizar_particao(text,text) TO govdata_arquivo_loader;
COMMIT;

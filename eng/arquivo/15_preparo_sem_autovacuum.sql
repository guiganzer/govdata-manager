\set ON_ERROR_STOP on
\pset pager off

-- Aplicar como postgres em dados_publicos, depois de 13_carga_por_anexo.sql.
-- Pode ser aplicado com o Loader parado; e idempotente.
--
-- Corrige os deadlocks 40P01 da troca final (log: "autovacuum: VACUUM ANALYZE
-- arquivo.stage_..."). O preparo recem-carregado disparava o autovacuum; a
-- finalizacao lia o preparo (lock fraco), o autovacuum ficava no meio e o
-- RENAME (lock exclusivo) fechava o ciclo.
--   * preparar_particao: preparo nasce com autovacuum desligado;
--   * finalizar_particao: pega ACCESS EXCLUSIVE no preparo antes de le-lo e
--     devolve o autovacuum padrao a particao definitiva;
--   * preparos ja existentes recebem autovacuum desligado agora.
BEGIN;
SET LOCAL lock_timeout = '10s';

DO $preflight$
BEGIN
    IF current_database() <> 'dados_publicos' THEN
        RAISE EXCEPTION 'Aplique em dados_publicos.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE oid = to_regprocedure('arquivo.finalizar_particao(text,text)')
                   AND prosrc LIKE '%ATTACH PARTITION%') THEN
        RAISE EXCEPTION 'Aplique antes 13_carga_por_anexo.sql.';
    END IF;
END
$preflight$;

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
    -- Sem autovacuum no preparo: so recebe INSERT e logo vira particao. Um VACUUM
    -- automatico no meio da troca final causava deadlock com o RENAME.
    EXECUTE format('ALTER TABLE arquivo.%I SET (autovacuum_enabled = false, toast.autovacuum_enabled = false)', v_stage);
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
    -- Lock exclusivo ANTES de qualquer leitura do preparo: quem chega primeiro com o
    -- lock forte nao entra em ciclo, e um autovacuum que esteja no caminho e
    -- cancelado pelo PostgreSQL em vez de gerar deadlock.
    EXECUTE format('LOCK TABLE arquivo.%I IN ACCESS EXCLUSIVE MODE', v_stage);
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
        -- Particao definitiva volta ao autovacuum padrao.
        EXECUTE format('ALTER TABLE arquivo.%I RESET (autovacuum_enabled, toast.autovacuum_enabled)', v_final);
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

DO $preparos$
DECLARE
    v_stage text;
    v_total int := 0;
BEGIN
    FOR v_stage IN SELECT relname FROM pg_class
                   WHERE relnamespace = 'arquivo'::regnamespace AND relkind = 'r' AND relname LIKE 'stage\_%'
    LOOP
        EXECUTE format('ALTER TABLE arquivo.%I SET (autovacuum_enabled = false, toast.autovacuum_enabled = false)', v_stage);
        v_total := v_total + 1;
    END LOOP;
    RAISE NOTICE 'Preparos existentes sem autovacuum: %.', v_total;
END
$preparos$;

REVOKE ALL ON FUNCTION arquivo.preparar_particao(text,text,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION arquivo.finalizar_particao(text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION arquivo.preparar_particao(text,text,uuid) TO govdata_arquivo_loader;
GRANT EXECUTE ON FUNCTION arquivo.finalizar_particao(text,text) TO govdata_arquivo_loader;
COMMIT;

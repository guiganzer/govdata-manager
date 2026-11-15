\set ON_ERROR_STOP on
\pset pager off

-- Aplicar como postgres em dados_publicos, depois de 15_preparo_sem_autovacuum.sql.
-- Pode ser aplicado com Loaders rodando: as proximas finalizacoes ja usam a versao nova.
--
-- Em cargas massivas (PNI), a finalizacao fazia tres passadas extras sobre cada particao
-- de 10-15 GB, todas no mesmo disco das cargas: count(*) do preparo, ANALYZE e, ao
-- devolver o autovacuum, um VACUUM completo logo em seguida. Agora:
--   * finalizar_particao nao varre o preparo nem roda ANALYZE, e mantem o autovacuum
--     desligado na particao recem-anexada;
--   * arquivo.reativar_autovacuum_particoes() devolve o autovacuum padrao a todas as
--     particoes que ficaram sem ele. Rode ao fim da carga; o PostgreSQL entao faz VACUUM
--     e ANALYZE em segundo plano.
BEGIN;
SET LOCAL lock_timeout = '10s';

DO $preflight$
BEGIN
    IF current_database() <> 'dados_publicos' THEN
        RAISE EXCEPTION 'Aplique em dados_publicos.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE oid = to_regprocedure('arquivo.finalizar_particao(text,text)')
                   AND prosrc LIKE '%LOCK TABLE%') THEN
        RAISE EXCEPTION 'Aplique antes 15_preparo_sem_autovacuum.sql.';
    END IF;
END
$preflight$;

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
    -- Sem count(*): cada lote grava linhas e checkpoint na mesma transacao, e a consulta inicial
    -- exige rows_loaded = expected_rows. A conferencia completa fica com o verify-data.
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
        -- Sem ANALYZE e com autovacuum ainda desligado: durante cargas massivas, cada passada
        -- extra sobre a particao disputa o disco. Depois da carga, rode
        -- arquivo.reativar_autovacuum_particoes() para devolver o padrao.
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

CREATE OR REPLACE FUNCTION arquivo.reativar_autovacuum_particoes()
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, arquivo
AS $function$
DECLARE
    v_rel text;
    v_total integer := 0;
BEGIN
    -- So particoes anexadas (relispartition); preparos em carga continuam sem autovacuum.
    FOR v_rel IN
        SELECT c.relname FROM pg_class c
        WHERE c.relnamespace = 'arquivo'::regnamespace AND c.relkind = 'r' AND c.relispartition
          AND c.reloptions @> ARRAY['autovacuum_enabled=false']
    LOOP
        EXECUTE format('ALTER TABLE arquivo.%I RESET (autovacuum_enabled, toast.autovacuum_enabled)', v_rel);
        v_total := v_total + 1;
    END LOOP;
    RETURN v_total;
END
$function$;

REVOKE ALL ON FUNCTION arquivo.finalizar_particao(text,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION arquivo.reativar_autovacuum_particoes() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION arquivo.finalizar_particao(text,text) TO govdata_arquivo_loader;
COMMIT;

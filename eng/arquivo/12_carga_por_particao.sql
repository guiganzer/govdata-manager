\set ON_ERROR_STOP on
\pset pager off

-- Aplicar como postgres em dados_publicos, depois do reset controlado.
-- Uma tabela de preparo por publicacao/particao permite checkpoints sem trocar
-- a tabela inteira do conjunto. A substituicao final e atomica.
BEGIN;
SET LOCAL lock_timeout = '10s';

DO $preflight$
BEGIN
    IF current_database() <> 'dados_publicos' OR
       (SELECT count(*) FROM arquivo.conjunto) <> 0 OR
       (SELECT count(*) FROM arquivo.publicacao) <> 0 OR
       (SELECT count(*) FROM arquivo.particao) <> 0 THEN
        RAISE EXCEPTION 'A instalacao exige dados_publicos e o schema arquivo vazio.';
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
BEGIN
    SELECT c.table_name, p.last_line INTO v_table, v_checkpoint
    FROM arquivo.particao p
    JOIN arquivo.publicacao pub ON pub.publication_id=p.publication_id
    JOIN arquivo.conjunto c ON c.conjunto_id=pub.conjunto_id
                           AND c.definition_version=pub.definition_version
    WHERE p.publication_id=p_publication_id AND p.partition_id=p_partition_id
      AND p.status='CARREGANDO' AND p.claim_token=p_claim_token;
    IF NOT FOUND THEN RAISE EXCEPTION 'Claim da particao nao encontrado.'; END IF;
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON arquivo.%I (particao_id)',
        'ix_part_' || substr(md5(v_table), 1, 24), v_table);
    v_stage := 'stage_' || substr(md5(p_publication_id || ':' || p_partition_id), 1, 32);
    EXECUTE format('CREATE TABLE IF NOT EXISTS arquivo.%I (LIKE arquivo.%I)', v_stage, v_table);
    EXECUTE format('GRANT SELECT, INSERT ON arquivo.%I TO govdata_arquivo_loader', v_stage);
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
    -- Serializa a substituicao do mesmo conjunto/particao. Leitores continuam
    -- vendo o estado anterior ate o commit, por MVCC.
    PERFORM pg_advisory_xact_lock(hashtextextended(v_conjunto || ':' || p_partition_id, 0));
    FOR v_old_table IN SELECT table_name FROM arquivo.conjunto
                       WHERE conjunto_id=v_conjunto LOOP
        EXECUTE format('DELETE FROM arquivo.%I WHERE particao_id=$1', v_old_table)
          USING p_partition_id;
    END LOOP;
    EXECUTE format('INSERT INTO arquivo.%I SELECT * FROM arquivo.%I', v_table, v_stage);
    EXECUTE format('DROP TABLE arquivo.%I RESTRICT', v_stage);

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

REVOKE ALL ON FUNCTION arquivo.preparar_particao(text,text,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION arquivo.finalizar_particao(text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION arquivo.preparar_particao(text,text,uuid) TO govdata_arquivo_loader;
GRANT EXECUTE ON FUNCTION arquivo.finalizar_particao(text,text) TO govdata_arquivo_loader;
COMMIT;

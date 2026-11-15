\set ON_ERROR_STOP on
\pset pager off

-- Somente leitura. Execute no banco dados_publicos antes de qualquer limpeza.
\echo == Banco e schema
SELECT current_database() AS banco, current_user AS usuario,
       clock_timestamp() AS consultado_em,
       to_regnamespace('arquivo') IS NOT NULL AS schema_arquivo_existe;

\echo == Processos que podem usar o schema arquivo
SELECT pid, usename, application_name, state,
       clock_timestamp() - xact_start AS transacao_ha,
       left(query, 180) AS consulta
FROM pg_stat_activity
WHERE datname = current_database() AND pid <> pg_backend_pid()
  AND (application_name ILIKE '%GovData%'
       OR application_name ILIKE '%NiFi%'
       OR query ILIKE '%arquivo.%')
ORDER BY xact_start NULLS LAST, pid;

\echo == Cadastro e checkpoints por status
SELECT 'conjunto' AS entidade, 'TOTAL' AS status, count(*) AS registros
FROM arquivo.conjunto
UNION ALL
SELECT 'publicacao', status, count(*) FROM arquivo.publicacao GROUP BY status
UNION ALL
SELECT 'particao', status, count(*) FROM arquivo.particao GROUP BY status
ORDER BY entidade, status;

\echo == Publicacoes e particoes por conjunto e versao
SELECT c.conjunto_id, c.definition_version, c.table_name,
       count(DISTINCT pub.publication_id) AS publicacoes,
       count(p.partition_id) AS particoes,
       coalesce(sum(p.rows_loaded), 0) AS linhas_marcadas_carregadas,
       coalesce(sum(p.expected_rows), 0) AS linhas_esperadas,
       count(*) FILTER (WHERE p.status = 'CARREGANDO') AS carregando,
       count(*) FILTER (WHERE p.status = 'ERRO') AS erros,
       pg_size_pretty(pg_total_relation_size(to_regclass(format('%I.%I', 'arquivo', c.table_name)))) AS tabela_total
FROM arquivo.conjunto c
LEFT JOIN arquivo.publicacao pub
  ON pub.conjunto_id = c.conjunto_id
 AND pub.definition_version = c.definition_version
LEFT JOIN arquivo.particao p ON p.publication_id = pub.publication_id
GROUP BY c.conjunto_id, c.definition_version, c.table_name
ORDER BY c.conjunto_id, c.definition_version;

\echo == Claims e falhas ainda registrados
SELECT pub.conjunto_id, p.publication_id, p.partition_id, p.status,
       p.rows_loaded, p.expected_rows, p.claim_token, p.lease_until,
       left(p.last_error, 240) AS erro
FROM arquivo.particao p
JOIN arquivo.publicacao pub USING (publication_id)
WHERE p.status IN ('CARREGANDO', 'ERRO') OR p.claim_token IS NOT NULL
ORDER BY pub.conjunto_id, p.partition_id;

\echo == Todas as relacoes fisicas do schema
SELECT c.relname AS relacao, c.relkind AS tipo,
       pg_size_pretty(pg_total_relation_size(c.oid)) AS tamanho_total,
       c.reltuples::bigint AS linhas_estimadas,
       CASE WHEN c.relname IN ('conjunto', 'publicacao', 'particao') THEN 'CONTROLE'
            WHEN r.table_name IS NOT NULL THEN 'DADOS_CADASTRADOS'
            ELSE 'SEM_CADASTRO' END AS classificacao
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN arquivo.conjunto r ON r.table_name = c.relname
WHERE n.nspname = 'arquivo' AND c.relkind IN ('r', 'p', 'v', 'm')
ORDER BY classificacao, pg_total_relation_size(c.oid) DESC;

\echo == Funcoes instaladas no schema arquivo
SELECT p.proname AS funcao, pg_get_function_identity_arguments(p.oid) AS argumentos
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'arquivo'
ORDER BY p.proname, argumentos;

\echo == Chaves estrangeiras externas que dependem de tabelas arquivo
SELECT con.conrelid::regclass AS tabela_externa, con.conname AS chave,
       con.confrelid::regclass AS tabela_arquivo
FROM pg_constraint con
JOIN pg_class target ON target.oid = con.confrelid
JOIN pg_namespace target_ns ON target_ns.oid = target.relnamespace
JOIN pg_class source ON source.oid = con.conrelid
JOIN pg_namespace source_ns ON source_ns.oid = source.relnamespace
WHERE con.contype = 'f' AND target_ns.nspname = 'arquivo'
  AND source_ns.nspname <> 'arquivo'
ORDER BY tabela_externa, chave;

\echo == Views externas que dependem de tabelas arquivo
SELECT DISTINCT view_ns.nspname AS schema_externo, view_rel.relname AS view_externa,
       source_rel.oid::regclass AS tabela_arquivo
FROM pg_depend dep
JOIN pg_rewrite rewrite ON rewrite.oid = dep.objid
JOIN pg_class view_rel ON view_rel.oid = rewrite.ev_class
JOIN pg_namespace view_ns ON view_ns.oid = view_rel.relnamespace
JOIN pg_class source_rel ON source_rel.oid = dep.refobjid
JOIN pg_namespace source_ns ON source_ns.oid = source_rel.relnamespace
WHERE dep.classid = 'pg_rewrite'::regclass
  AND dep.refclassid = 'pg_class'::regclass
  AND source_ns.nspname = 'arquivo'
  AND view_ns.nspname <> 'arquivo'
ORDER BY schema_externo, view_externa, tabela_arquivo;

\echo == Tamanho total do schema arquivo
SELECT pg_size_pretty(coalesce(sum(pg_total_relation_size(c.oid)), 0)::bigint) AS tamanho_total
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'arquivo' AND c.relkind IN ('r', 'p', 'm');

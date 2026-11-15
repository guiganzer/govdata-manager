-- Execute as postgres in dados_publicos before 01_schema.sql.
DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'govdata_arquivo_loader') THEN
        CREATE ROLE govdata_arquivo_loader NOLOGIN;
    END IF;
END
$role$;

GRANT CONNECT ON DATABASE dados_publicos TO govdata_arquivo_loader;

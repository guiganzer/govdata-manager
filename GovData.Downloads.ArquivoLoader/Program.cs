using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Npgsql;
using NpgsqlTypes;

namespace GovData.Downloads.ArquivoLoader;

internal static partial class Program
{
    private static readonly Regex SafeIdentifier = new("^(?:[a-z][a-z0-9_]{0,62}|_[a-z][a-z0-9_]{0,61})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
            {
                PrintUsage();
                return 0;
            }

            var command = args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal) ? args[0] : "load";
            if (command is not ("load" or "register" or "register-run" or "check-catalog" or "verify-data" or "status"))
                throw new ArgumentException($"Comando desconhecido: {command}");

            var configPath = ReadOption(args, "--config")
                ?? Environment.GetEnvironmentVariable("GOVDATA_ARQUIVO_CONFIG")
                ?? "/etc/govdata-manager/arquivo-loader.json";
            var config = await LoadConfigAsync(configPath);
            RunScope? scope = null;
            if (command is "load" or "register-run" ||
                (command is "verify-data" or "check-catalog" && ReadOption(args, "--run-dir") is not null))
            {
                var runDir = ReadOption(args, "--run-dir")
                    ?? throw new ArgumentException($"{command} exige --run-dir <pasta-da-organizacao>.");
                scope = await LoadRunScopeAsync(runDir);
                config.ParquetRoot = scope.ReleasesPath;
                config.PublicationStatePath = scope.PublicationStatePath;
            }
            ValidateConfig(config, command != "status");
            if (command is "register" or "register-run" or "check-catalog") ValidateCatalogConfig(config);

            var password = (await File.ReadAllTextAsync(config.Postgres.PasswordFile)).TrimEnd('\r', '\n');
            if (password.Length == 0) throw new InvalidDataException("O arquivo de senha do PostgreSQL está vazio.");
            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = config.Postgres.Host,
                Port = config.Postgres.Port,
                Database = config.Postgres.Database,
                Username = config.Postgres.Username,
                Password = password,
                Timeout = config.Postgres.ConnectionTimeoutSeconds,
                CommandTimeout = config.Postgres.CommandTimeoutSeconds,
                KeepAlive = 30,
                Pooling = true,
                ApplicationName = "GovData.ArquivoLoader"
            };

            await using var pg = new NpgsqlConnection(csb.ConnectionString);
            await pg.OpenAsync();
            // Cada lote grava linhas e checkpoint na mesma transação. Sem esperar o fsync do WAL,
            // uma queda perde só os últimos lotes inteiros (linhas e checkpoint juntos), que
            // são recarregados a partir do checkpoint confirmado; nunca deixa estado parcial.
            await using (var session = new NpgsqlCommand("SET synchronous_commit TO off", pg))
                await session.ExecuteNonQueryAsync();

            if (command == "status")
            {
                var limit = ParsePositiveInt(ReadOption(args, "--limit"), 100, "--limit");
                return await PrintStatusAsync(pg, limit);
            }

            if (command == "register-run")
            {
                foreach (var publicationId in scope!.PublicationIds)
                    await RegisterPublicationAsync(pg, config, publicationId, null);
                Console.WriteLine($"Lote cadastrado: {scope.PublicationIds.Length} publicações de {scope.Directory}.");
                return 0;
            }

            if (command == "register")
            {
                var publicationId = ReadOption(args, "--publication")
                    ?? throw new ArgumentException("register exige --publication <id-de-64-hex>.");
                var priorityText = ReadOption(args, "--priority");
                int? priority = priorityText is null ? null : int.TryParse(priorityText, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var parsedPriority) ? parsedPriority :
                    throw new ArgumentException("--priority precisa ser um inteiro.");
                await RegisterPublicationAsync(pg, config, publicationId, priority);
                return 0;
            }

            if (command == "check-catalog")
            {
                var publicationId = ReadOption(args, "--publication");
                var checkAll = args.Contains("--all", StringComparer.Ordinal);
                if (checkAll == (publicationId is not null))
                    throw new ArgumentException("Use check-catalog --publication <id> ou check-catalog --all.");
                return await CheckCatalogAsync(pg, config, publicationId, checkAll);
            }

            if (command == "verify-data")
            {
                var publicationId = ReadOption(args, "--publication")
                    ?? throw new ArgumentException("verify-data exige --publication <id-de-64-hex>.");
                return await VerifyDataAsync(pg, config, publicationId);
            }

            var state = await LoadPublicationStateAsync(scope!.PublicationStatePath);
            foreach (var id in scope.PublicationIds)
            {
                var release = state.Releases.Single(x => x.Id == id);
                var issues = new List<string>();
                await CheckDatabaseRegistrationAsync(pg, config, release, issues, () => { });
                if (issues.Count != 0)
                    throw new InvalidDataException($"Cadastro de {id} divergente: {string.Join("; ", issues)}");
            }
            var maxPartitions = ParsePositiveInt(ReadOption(args, "--max-partitions"), 1, "--max-partitions");
            Console.WriteLine($"Conectado ao PostgreSQL {config.Postgres.Host}:{config.Postgres.Port}/{config.Postgres.Database}; chunk={config.ChunkRows}; DuckDB threads={config.DuckDb.Threads}; memória={config.DuckDb.MemoryLimit}.");
            Console.Out.Flush();

            var loaded = 0;
            while (loaded < maxPartitions)
            {
                var work = await ClaimNextAsync(pg, config.LeaseMinutes, scope!.PublicationIds);
                if (work is null)
                {
                    Console.WriteLine("Sem partições pendentes para carga.");
                    Console.Out.Flush();
                    break;
                }

                Console.WriteLine($"Iniciando {work.ConjuntoId}/{work.PartitionId} · publicação {work.PublicationId} · retomada após linha {work.LastLine}/{work.ExpectedRows}.");
                Console.Out.Flush();
                try
                {
                    await LoadPartitionAsync(pg, work, config);
                    loaded++;
                    Console.WriteLine($"CARREGADA {work.ConjuntoId}/{work.PartitionId}: {work.ExpectedRows:N0} linhas.");
                    Console.Out.Flush();
                }
                catch (Exception ex)
                {
                    await MarkFailedAsync(pg, work, ex);
                    Console.Error.WriteLine($"ERRO {work.ConjuntoId}/{work.PartitionId}: {ex.Message}");
                    return 1;
                }
            }

            Console.WriteLine($"Execução encerrada; partições concluídas nesta chamada: {loaded}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<LoaderConfig> LoadConfigAsync(string path)
    {
        var full = Path.GetFullPath(path);
        await using var input = File.OpenRead(full);
        return await JsonSerializer.DeserializeAsync<LoaderConfig>(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"Configuração vazia: {full}");
    }

    private static async Task<int> PrintStatusAsync(NpgsqlConnection pg, int limit)
    {
        var items = new List<object>();
        const string sql = """
            SELECT pub.publication_id, pub.conjunto_id, pub.definition_version,
                   pub.status AS publication_status, pub.priority,
                   part.partition_id, part.status AS partition_status,
                   part.rows_loaded, part.expected_rows, part.last_line,
                   left(part.last_error, 2048) AS last_error, part.updated_at
            FROM arquivo.publicacao pub
            JOIN arquivo.particao part USING (publication_id)
            ORDER BY pub.priority DESC, pub.publication_id, part.partition_id
            LIMIT @limit
            """;
        await using var cmd = new NpgsqlCommand(sql, pg);
        cmd.Parameters.AddWithValue("limit", limit);
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                items.Add(new
                {
                    publicationId = reader.GetString(0),
                    conjuntoId = reader.GetString(1),
                    definitionVersion = reader.GetInt32(2),
                    publicationStatus = reader.GetString(3),
                    priority = reader.GetInt32(4),
                    partitionId = reader.GetString(5),
                    partitionStatus = reader.GetString(6),
                    rowsLoaded = reader.GetInt64(7),
                    expectedRows = reader.GetInt64(8),
                    lastLine = reader.GetInt64(9),
                    lastError = reader.IsDBNull(10) ? null : reader.GetString(10),
                    updatedAt = reader.GetValue(11).ToString()
                });
            }
        }

        const string summarySql = """
            SELECT count(*) AS partitions,
                   coalesce(sum(rows_loaded) FILTER (WHERE status<>'SUBSTITUIDA'), 0)::bigint AS rows_loaded,
                   coalesce(sum(expected_rows) FILTER (WHERE status<>'SUBSTITUIDA'), 0)::bigint AS expected_rows,
                   count(*) FILTER (WHERE status='ERRO') AS errors,
                   count(*) FILTER (WHERE status='CARREGADA') AS loaded_partitions,
                   count(*) FILTER (WHERE status IN ('PENDENTE','CARREGANDO')) AS pending_partitions,
                   count(*) FILTER (WHERE status='SUBSTITUIDA') AS replaced_partitions
            FROM arquivo.particao
            """;
        await using var summaryCommand = new NpgsqlCommand(summarySql, pg);
        await using var summaryReader = await summaryCommand.ExecuteReaderAsync();
        if (!await summaryReader.ReadAsync())
            throw new InvalidDataException("A consulta de resumo do schema arquivo não retornou resultado.");
        var summary = new
        {
            partitions = summaryReader.GetInt64(0),
            rowsLoaded = summaryReader.GetInt64(1),
            expectedRows = summaryReader.GetInt64(2),
            errors = summaryReader.GetInt64(3),
            loadedPartitions = summaryReader.GetInt64(4),
            pendingPartitions = summaryReader.GetInt64(5),
            replacedPartitions = summaryReader.GetInt64(6)
        };
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, summary, items }));
        return 0;
    }

    private static void ValidateConfig(LoaderConfig c, bool requireParquetRoot)
    {
        if (string.IsNullOrWhiteSpace(c.Postgres.Host) || c.Postgres.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(c.Postgres.Database) || string.IsNullOrWhiteSpace(c.Postgres.Username) ||
            !Path.IsPathRooted(c.Postgres.PasswordFile) || !File.Exists(c.Postgres.PasswordFile))
            throw new InvalidDataException("Configure host, porta, banco, usuário e arquivo de senha do PostgreSQL.");
        if (requireParquetRoot && (!Path.IsPathRooted(c.ParquetRoot) || !Directory.Exists(c.ParquetRoot)))
            throw new InvalidDataException("parquetRoot precisa apontar para o diretório absoluto de releases Parquet.");
        var memory = Regex.Match(c.DuckDb.MemoryLimit, "^([1-9][0-9]*)(MB|GB)$", RegexOptions.CultureInvariant);
        var memoryMb = memory.Success && long.TryParse(memory.Groups[1].Value, out var amount)
            ? amount * (memory.Groups[2].Value == "GB" ? 1024L : 1L) : 0L;
        if (c.ChunkRows is < 1000 or > 100000 || c.LeaseMinutes is < 30 or > 1440 ||
            c.DuckDb.Threads is < 1 or > 8 || memoryMb is < 512 or > 8192 ||
            c.Postgres.ConnectionTimeoutSeconds is < 1 or > 300 || c.Postgres.CommandTimeoutSeconds < 0)
            throw new InvalidDataException("Chunk, lease, threads ou limite de memória fora dos intervalos permitidos.");
        if (!Path.IsPathRooted(c.DuckDb.TempDirectory))
            throw new InvalidDataException("duckDb.tempDirectory deve ser absoluto.");
    }

    private static async Task<WorkItem?> ClaimNextAsync(NpgsqlConnection pg, int leaseMinutes, string[] publicationIds)
    {
        await using var tx = await pg.BeginTransactionAsync();
        const string select = """
            SELECT p.publication_id, p.conjunto_id, p.partition_id, p.relative_path,
                   p.expected_rows, p.rows_loaded, p.last_line, c.table_name, c.columns
            FROM arquivo.particao AS p
            JOIN arquivo.publicacao AS pub ON pub.publication_id = p.publication_id
            JOIN arquivo.conjunto AS c
              ON c.conjunto_id = pub.conjunto_id
             AND c.definition_version = pub.definition_version
            WHERE p.publication_id = ANY(@publication_ids)
              AND pub.status IN ('PENDENTE', 'CARREGANDO')
              AND (p.status = 'PENDENTE' OR (p.status = 'CARREGANDO' AND p.lease_until < clock_timestamp()))
            ORDER BY pub.priority DESC, pub.created_at, p.partition_id
            FOR UPDATE OF p SKIP LOCKED
            LIMIT 1
            """;
        WorkItem? item = null;
        await using (var cmd = new NpgsqlCommand(select, pg, tx))
        {
            cmd.Parameters.AddWithValue("publication_ids", publicationIds);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
                item = new WorkItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetString(7), r.GetFieldValue<string[]>(8));
        }

        if (item is null)
        {
            await tx.CommitAsync();
            return null;
        }

        var claimToken = Guid.NewGuid();
        const string claim = """
            UPDATE arquivo.particao
            SET status='CARREGANDO', lease_until=clock_timestamp() + make_interval(mins => @lease_minutes),
                claim_token=@claim_token, attempts=attempts+1,
                started_at=COALESCE(started_at, clock_timestamp()), updated_at=clock_timestamp(), last_error=NULL
            WHERE publication_id=@publication_id AND partition_id=@partition_id
            """;
        await using (var cmd = new NpgsqlCommand(claim, pg, tx))
        {
            cmd.Parameters.AddWithValue("lease_minutes", leaseMinutes);
            cmd.Parameters.AddWithValue("claim_token", claimToken);
            cmd.Parameters.AddWithValue("publication_id", item.PublicationId);
            cmd.Parameters.AddWithValue("partition_id", item.PartitionId);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand("UPDATE arquivo.publicacao SET status='CARREGANDO', updated_at=clock_timestamp() WHERE publication_id=@id", pg, tx))
        {
            cmd.Parameters.AddWithValue("id", item.PublicationId);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return item with { ClaimToken = claimToken };
    }

    private static async Task LoadPartitionAsync(NpgsqlConnection pg, WorkItem work, LoaderConfig config)
    {
        ValidateSchemaNames(work);
        string stageTable;
        await using (var prepare = new NpgsqlCommand("SELECT arquivo.preparar_particao(@publication_id, @partition_id, @claim_token)", pg))
        {
            prepare.Parameters.AddWithValue("publication_id", work.PublicationId);
            prepare.Parameters.AddWithValue("partition_id", work.PartitionId);
            prepare.Parameters.AddWithValue("claim_token", work.ClaimToken);
            stageTable = (string?)await prepare.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("O PostgreSQL não retornou a tabela de preparação.");
        }
        if (!SafeIdentifier.IsMatch(stageTable))
            throw new InvalidDataException("Nome da tabela de preparação inválido.");
        var root = Path.GetFullPath(config.ParquetRoot);
        if (Path.IsPathRooted(work.RelativePath) || work.RelativePath.Split(['/', '\\']).Any(x => x == ".."))
            throw new InvalidDataException("O caminho relativo da partição não é seguro.");
        var parquet = Path.GetFullPath(Path.Combine(root, work.RelativePath));
        var relativeCheck = Path.GetRelativePath(root, parquet);
        if (relativeCheck == ".." || relativeCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(parquet))
            throw new FileNotFoundException("Parquet não encontrado sob parquetRoot.", parquet);

        Directory.CreateDirectory(config.DuckDb.TempDirectory);
        using var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        ExecuteDuck(duck, $"SET threads={config.DuckDb.Threads.ToString(CultureInfo.InvariantCulture)}");
        ExecuteDuck(duck, $"SET memory_limit={DuckLiteral(config.DuckDb.MemoryLimit)}");
        ExecuteDuck(duck, "SET preserve_insertion_order=true");
        ExecuteDuck(duck, $"SET temp_directory={DuckLiteral(Path.GetFullPath(config.DuckDb.TempDirectory))}");

        var fields = string.Join(",", work.Columns.Select(QuoteIdentifier));
        var sql = $"SELECT CAST({QuoteIdentifier("_linha")} AS BIGINT) AS {QuoteIdentifier("_linha")},{fields} FROM read_parquet({DuckLiteral(parquet)})";
        using var cmd = duck.CreateCommand();
        cmd.CommandText = sql;
        cmd.UseStreamingMode = true;
        using var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess);
        if (reader.FieldCount != work.Columns.Length + 1 || reader.GetName(0) != "_linha")
            throw new InvalidDataException("As colunas do Parquet não correspondem ao registro do conjunto.");
        for (var i = 0; i < work.Columns.Length; i++)
            if (!string.Equals(reader.GetName(i + 1), work.Columns[i], StringComparison.Ordinal))
                throw new InvalidDataException($"Coluna Parquet divergente na posição {i}: esperado {work.Columns[i]}, recebido {reader.GetName(i + 1)}.");

        var copyColumns = new[] { "publicacao_id", "particao_id", "_linha" }.Concat(work.Columns).ToArray();
        var copySql = $"COPY arquivo.{QuoteIdentifier(stageTable)} ({string.Join(",", copyColumns.Select(QuoteIdentifier))}) FROM STDIN (FORMAT BINARY)";
        var lastLine = work.LastLine;
        long previousSourceLine = 0;
        var chunkCount = 0;
        NpgsqlTransaction? tx = null;
        NpgsqlBinaryImporter? importer = null;

        async Task CommitChunkAsync(long throughLine)
        {
            if (tx is null || importer is null || chunkCount == 0) return;
            await importer.CompleteAsync();
            await importer.DisposeAsync();
            importer = null;
            const string checkpoint = """
                UPDATE arquivo.particao
                SET rows_loaded=@rows, last_line=@line,
                    lease_until=clock_timestamp() + make_interval(mins => @lease_minutes), updated_at=clock_timestamp()
                WHERE publication_id=@publication_id AND partition_id=@partition_id AND status='CARREGANDO' AND claim_token=@claim_token
                """;
            await using (var update = new NpgsqlCommand(checkpoint, pg, tx))
            {
                update.Parameters.AddWithValue("rows", throughLine);
                update.Parameters.AddWithValue("line", throughLine);
                update.Parameters.AddWithValue("lease_minutes", config.LeaseMinutes);
                update.Parameters.AddWithValue("publication_id", work.PublicationId);
                update.Parameters.AddWithValue("partition_id", work.PartitionId);
                update.Parameters.AddWithValue("claim_token", work.ClaimToken);
                if (await update.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Checkpoint da partição não foi atualizado.");
            }
            await tx.CommitAsync();
            await tx.DisposeAsync();
            tx = null;
            chunkCount = 0;
            lastLine = throughLine;
            Console.WriteLine($"Checkpoint {work.PartitionId}: {lastLine:N0}/{work.ExpectedRows:N0} linhas.");
                Console.Out.Flush();
        }

        try
        {
            while (reader.Read())
            {
                // A consulta DuckDB já converte _linha para BIGINT; evita texto e parse por linha.
                if (reader.IsDBNull(0)) throw new InvalidDataException("Valor _linha nulo.");
                var line = reader.GetInt64(0);
                if (line < 1) throw new InvalidDataException($"Valor _linha inválido: {line}.");
                if (line != previousSourceLine + 1)
                    throw new InvalidDataException($"Sequência Parquet _linha inválida: esperado {previousSourceLine + 1}, recebido {line}.");
                previousSourceLine = line;
                if (line <= lastLine) continue;
                var expectedCheckpointLine = checked(lastLine + chunkCount + 1L);
                if (line != expectedCheckpointLine)
                    throw new InvalidDataException($"Sequência _linha interrompida: esperado {expectedCheckpointLine}, recebido {line}.");

                if (tx is null)
                {
                    tx = await pg.BeginTransactionAsync();
                    importer = await pg.BeginBinaryImportAsync(copySql);
                }
                await importer!.StartRowAsync();
                await importer.WriteAsync(work.PublicationId, NpgsqlDbType.Text);
                await importer.WriteAsync(work.PartitionId, NpgsqlDbType.Text);
                await importer.WriteAsync(line, NpgsqlDbType.Bigint);
                for (var i = 0; i < work.Columns.Length; i++)
                {
                    if (reader.IsDBNull(i + 1)) await importer.WriteNullAsync();
                    else await importer.WriteAsync(reader.GetString(i + 1), NpgsqlDbType.Text);
                }
                chunkCount++;
                if (chunkCount >= config.ChunkRows) await CommitChunkAsync(line);
            }
            await CommitChunkAsync(lastLine + chunkCount);
            if (lastLine != work.ExpectedRows)
                throw new InvalidDataException($"Contagem final divergente: parquet/cursor={lastLine:N0}; esperado={work.ExpectedRows:N0}.");

            // A troca final (RENAME + ATTACH) precisa de lock exclusivo no preparo. Uma sessao
            // alheia (cliente SQL navegando o schema, outra carga) pode causar deadlock ou espera
            // longa; as linhas ja estao no preparo, entao basta repetir a transacao final.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var finish = await pg.BeginTransactionAsync();
                    await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '20s'", pg, finish))
                        await timeout.ExecuteNonQueryAsync();
                    const string markPartition = """
                        UPDATE arquivo.particao
                        SET status='CARREGADA', rows_loaded=@rows, last_line=@line, claim_token=NULL, lease_until=NULL,
                            completed_at=clock_timestamp(), updated_at=clock_timestamp(), last_error=NULL
                        WHERE publication_id=@publication_id AND partition_id=@partition_id AND status='CARREGANDO'
                          AND claim_token=@claim_token
                        """;
                    await using (var update = new NpgsqlCommand(markPartition, pg, finish))
                    {
                        update.Parameters.AddWithValue("rows", work.ExpectedRows);
                        update.Parameters.AddWithValue("line", work.ExpectedRows);
                        update.Parameters.AddWithValue("publication_id", work.PublicationId);
                        update.Parameters.AddWithValue("partition_id", work.PartitionId);
                        update.Parameters.AddWithValue("claim_token", work.ClaimToken);
                        if (await update.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Estado final da partição não foi atualizado.");
                    }
                    const string markPublication = """
                        UPDATE arquivo.publicacao pub SET
                        status=CASE WHEN EXISTS (
                                SELECT 1 FROM arquivo.particao p WHERE p.publication_id=pub.publication_id AND p.status='ERRO'
                            ) THEN 'ERRO' WHEN NOT EXISTS (
                                SELECT 1 FROM arquivo.particao p WHERE p.publication_id=pub.publication_id AND p.status <> 'CARREGADA'
                            ) THEN 'CARREGADA' ELSE 'CARREGANDO' END,
                            updated_at=clock_timestamp()
                        WHERE pub.publication_id=@publication_id
                        """;
                    await using (var update = new NpgsqlCommand(markPublication, pg, finish))
                    {
                        update.Parameters.AddWithValue("publication_id", work.PublicationId);
                        await update.ExecuteNonQueryAsync();
                    }
                    await using (var publish = new NpgsqlCommand("SELECT arquivo.finalizar_particao(@publication_id, @partition_id)", pg, finish))
                    {
                        publish.Parameters.AddWithValue("publication_id", work.PublicationId);
                        publish.Parameters.AddWithValue("partition_id", work.PartitionId);
                        await publish.ExecuteScalarAsync();
                    }
                    await finish.CommitAsync();
                    break;
                }
                catch (PostgresException ex) when (ex.SqlState is "40P01" or "55P03" && attempt < 6)
                {
                    Console.Error.WriteLine($"AVISO {work.ConjuntoId}/{work.PartitionId}: finalizacao bloqueada ({ex.SqlState}); tentativa {attempt + 1} em {attempt * 5}s.");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 5));
                }
            }
        }
        finally
        {
            if (importer is not null) await importer.DisposeAsync();
            if (tx is not null)
            {
                try { await tx.RollbackAsync(); } catch { }
                await tx.DisposeAsync();
            }
        }
    }

    private static async Task MarkFailedAsync(NpgsqlConnection pg, WorkItem work, Exception ex)
    {
        var message = ex.ToString();
        if (message.Length > 4000) message = message[..4000];
        await using var tx = await pg.BeginTransactionAsync();
        var updated = 0;
        await using (var cmd = new NpgsqlCommand("UPDATE arquivo.particao SET status='ERRO', claim_token=NULL, lease_until=NULL, last_error=@error, updated_at=clock_timestamp() WHERE publication_id=@publication_id AND partition_id=@partition_id AND claim_token=@claim_token", pg, tx))
        {
            cmd.Parameters.AddWithValue("error", message);
            cmd.Parameters.AddWithValue("publication_id", work.PublicationId);
            cmd.Parameters.AddWithValue("partition_id", work.PartitionId);
            cmd.Parameters.AddWithValue("claim_token", work.ClaimToken);
            updated = await cmd.ExecuteNonQueryAsync();
        }
        if (updated == 1)
        {
            await using var cmd = new NpgsqlCommand("UPDATE arquivo.publicacao SET status='ERRO', updated_at=clock_timestamp() WHERE publication_id=@publication_id", pg, tx);
            cmd.Parameters.AddWithValue("publication_id", work.PublicationId);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private static void ValidateSchemaNames(WorkItem work)
    {
        if (!SafeIdentifier.IsMatch(work.TableName) || work.Columns.Length == 0 || work.RowsLoaded != work.LastLine ||
            work.Columns.Any(c => !SafeIdentifier.IsMatch(c) || c is "publicacao_id" or "particao_id" or "_linha") ||
            work.Columns.Distinct(StringComparer.Ordinal).Count() != work.Columns.Length)
            throw new InvalidDataException("Identificadores do schema arquivo inválidos ou duplicados.");
    }

    private static void ExecuteDuck(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string DuckLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static string? ReadOption(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == option)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Falta valor para {option}.");
                return args[i + 1];
            }
        }
        return null;
    }

    private static int ParsePositiveInt(string? value, int fallback, string name)
    {
        if (value is null) return fallback;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 100
            ? parsed : throw new ArgumentException($"{name} precisa ser um inteiro entre 1 e 100.");
    }

    private static void PrintUsage() => Console.WriteLine("Uso: GovData.Downloads.ArquivoLoader register-run --run-dir DIR [--config FILE] | load --run-dir DIR [--max-partitions N] [--config FILE] | status [--config FILE] | verify-data --publication ID [--config FILE]");

    private sealed class LoaderConfig
    {
        public PostgresConfig Postgres { get; set; } = new();
        public string ParquetRoot { get; set; } = "";
        public string PublicationStatePath { get; set; } = "";
        public DuckDbConfig DuckDb { get; set; } = new();
        public int ChunkRows { get; set; } = 20000;
        public int LeaseMinutes { get; set; } = 360;
    }

    private sealed class PostgresConfig
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 5432;
        public string Database { get; set; } = "dados_publicos";
        public string Username { get; set; } = "govdata_arquivo_loader";
        public string PasswordFile { get; set; } = "/etc/govdata-manager/arquivo-loader.password";
        public int ConnectionTimeoutSeconds { get; set; } = 15;
        public int CommandTimeoutSeconds { get; set; } = 0;
    }

    private sealed class DuckDbConfig
    {
        public int Threads { get; set; } = 1;
        public string MemoryLimit { get; set; } = "768MB";
        public string TempDirectory { get; set; } = "/mnt/dados/govdata-manager/dados/server/arquivo/tmp";
    }

    private sealed record WorkItem(string PublicationId, string ConjuntoId, string PartitionId, string RelativePath,
        long ExpectedRows, long RowsLoaded, long LastLine, string TableName, string[] Columns, Guid ClaimToken = default);
}

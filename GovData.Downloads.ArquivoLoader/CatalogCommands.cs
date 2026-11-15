using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Npgsql;

namespace GovData.Downloads.ArquivoLoader;

internal static partial class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true };

    private static void ValidateCatalogConfig(LoaderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.PublicationStatePath) || !Path.IsPathRooted(config.PublicationStatePath) ||
            !File.Exists(config.PublicationStatePath))
            throw new InvalidDataException("publicationStatePath precisa apontar para server/storage/publications-v3.json.");
    }

    private static async Task RegisterPublicationAsync(NpgsqlConnection pg, LoaderConfig config, string publicationId, int? priority)
    {
        if (!Regex.IsMatch(publicationId, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("--publication deve ser um ID hexadecimal minúsculo com 64 caracteres.");

        var manifest = await LoadPublicationStateAsync(config.PublicationStatePath);
        var release = manifest.Releases.SingleOrDefault(x => x.Id == publicationId)
            ?? throw new InvalidDataException($"Publicacao {publicationId} nao existe em publications-v3.json.");
        if (release.PurgedAt is not null) throw new InvalidDataException("A publicacao foi expurgada e nao pode ser cadastrada.");
        if (release.Partitions.Count == 0) throw new InvalidDataException("A publicacao nao possui particoes.");

        var columns = release.Partitions[0].Columns;
        if (columns.Length == 0 || release.Partitions.Any(x => !x.Columns.SequenceEqual(columns, StringComparer.Ordinal)))
            throw new InvalidDataException("Particoes da publicacao nao possuem um schema uniforme.");

        var parts = new List<RegistrationPartition>();
        foreach (var part in release.Partitions)
        {
            var relativePath = BuildRelativeParquetPath(release, part);
            var actual = InspectParquet(ResolveParquetPath(config.ParquetRoot, relativePath), part.Columns, config.DuckDb);
            if (actual.Rows != part.Rows)
                throw new InvalidDataException($"Contagem Parquet divergente em {part.ArquivoId}: manifesto={part.Rows}, parquet={actual.Rows}.");
            if (!Regex.IsMatch(part.OriginalSha256, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException($"SHA-256 de origem ausente ou invalido no manifesto para {part.ArquivoId}.");
            parts.Add(new RegistrationPartition(part.ArquivoId, relativePath, part.Rows, part.OriginalSha256, part.Columns));
            Console.WriteLine($"Parquet confirmado: {part.ArquivoId}, {actual.Rows:N0} linhas, {actual.Columns.Length} colunas.");
        }

        await using var tx = await pg.BeginTransactionAsync();
        string tableName;
        await using (var cmd = new NpgsqlCommand(
            "SELECT arquivo.registrar_conjunto(@conjunto_id, @columns, @definition_version)", pg, tx))
        {
            cmd.Parameters.AddWithValue("conjunto_id", release.ConjuntoId);
            cmd.Parameters.AddWithValue("columns", columns);
            cmd.Parameters.AddWithValue("definition_version", release.VersaoDefinicao);
            tableName = (string?)await cmd.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("arquivo.registrar_conjunto nao retornou o nome da tabela.");
        }

        const string upsertPublication = """
            INSERT INTO arquivo.publicacao(publication_id, conjunto_id, definition_version, priority)
            VALUES (@publication_id, @conjunto_id, @definition_version, COALESCE(@priority, 0))
            ON CONFLICT (publication_id) DO UPDATE SET
                definition_version=EXCLUDED.definition_version,
                priority=COALESCE(@priority, arquivo.publicacao.priority), updated_at=clock_timestamp()
            WHERE arquivo.publicacao.conjunto_id=EXCLUDED.conjunto_id
            RETURNING publication_id
            """;
        await using (var cmd = new NpgsqlCommand(upsertPublication, pg, tx))
        {
            cmd.Parameters.AddWithValue("publication_id", release.Id);
            cmd.Parameters.AddWithValue("conjunto_id", release.ConjuntoId);
            cmd.Parameters.AddWithValue("definition_version", release.VersaoDefinicao);
            cmd.Parameters.AddWithValue("priority", priority is null ? DBNull.Value : priority.Value);
            if (await cmd.ExecuteScalarAsync() is null)
                throw new InvalidDataException("O ID da publicacao ja esta vinculado a outro conjunto no schema arquivo.");
        }

        foreach (var part in parts)
        {
            const string insertPart = """
                INSERT INTO arquivo.particao(publication_id, conjunto_id, definition_version, partition_id,
                                             relative_path, expected_rows, source_sha256)
                VALUES (@publication_id, @conjunto_id, @definition_version, @partition_id,
                        @relative_path, @expected_rows, @source_sha256)
                ON CONFLICT (publication_id, partition_id) DO NOTHING
                """;
            await using (var cmd = new NpgsqlCommand(insertPart, pg, tx))
            {
                cmd.Parameters.AddWithValue("publication_id", release.Id);
                cmd.Parameters.AddWithValue("conjunto_id", release.ConjuntoId);
                cmd.Parameters.AddWithValue("definition_version", release.VersaoDefinicao);
                cmd.Parameters.AddWithValue("partition_id", part.Id);
                cmd.Parameters.AddWithValue("relative_path", part.RelativePath);
                cmd.Parameters.AddWithValue("expected_rows", part.Rows);
                cmd.Parameters.AddWithValue("source_sha256", part.SourceSha256);
                await cmd.ExecuteNonQueryAsync();
            }

            bool needsSourceHash;
            await using (var cmd = new NpgsqlCommand("""
                SELECT conjunto_id, definition_version, relative_path, expected_rows, source_sha256
                FROM arquivo.particao WHERE publication_id=@publication_id AND partition_id=@partition_id
                FOR UPDATE
                """, pg, tx))
            {
                cmd.Parameters.AddWithValue("publication_id", release.Id);
                cmd.Parameters.AddWithValue("partition_id", part.Id);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    throw new InvalidDataException($"Particao {part.Id} nao foi persistida no banco.");
                if (reader.GetString(0) != release.ConjuntoId || reader.GetInt32(1) != release.VersaoDefinicao ||
                    reader.GetString(2) != part.RelativePath || reader.GetInt64(3) != part.Rows)
                    throw new InvalidDataException($"Particao {part.Id} ja cadastrada com conjunto, versao, caminho ou linhas diferentes; nenhum estado de carga foi alterado.");
                needsSourceHash = reader.IsDBNull(4) ||
                    !string.Equals(reader.GetString(4), part.SourceSha256, StringComparison.OrdinalIgnoreCase);
            }
            if (needsSourceHash)
            {
                await using var update = new NpgsqlCommand(
                    "UPDATE arquivo.particao SET source_sha256=@sha, updated_at=clock_timestamp() WHERE publication_id=@pub AND partition_id=@part", pg, tx);
                update.Parameters.AddWithValue("sha", part.SourceSha256);
                update.Parameters.AddWithValue("pub", release.Id);
                update.Parameters.AddWithValue("part", part.Id);
                await update.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        Console.WriteLine($"CADASTRADA: {release.ConjuntoId} / {release.Id}; particoes={parts.Count}; tabela=arquivo.{tableName}; prioridade={priority?.ToString(CultureInfo.InvariantCulture) ?? "preservada (novo=0)"}.");
        Console.WriteLine("Status e checkpoints existentes foram preservados; este comando somente cadastra metadados.");
    }

    private static async Task<int> CheckCatalogAsync(NpgsqlConnection pg, LoaderConfig config, string? publicationId, bool checkAll)
    {
        var manifest = await LoadPublicationStateAsync(config.PublicationStatePath);
        var active = manifest.Releases.Where(x => x.PurgedAt is null).ToDictionary(x => x.Id, StringComparer.Ordinal);
        List<ManifestRelease> selected;
        if (checkAll) selected = active.Values.OrderBy(x => x.ConjuntoId, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        else if (active.TryGetValue(publicationId!, out var one)) selected = [one];
        else throw new InvalidDataException($"Publicacao ativa {publicationId} nao encontrada no manifesto.");

        var problems = 0;
        var checkedPartitions = 0;
        foreach (var release in selected)
        {
            var issues = new List<string>();
            try { await CheckDatabaseRegistrationAsync(pg, config, release, issues, () => checkedPartitions++); }
            catch (Exception ex) { issues.Add("PostgreSQL: " + ex.Message); }
            Console.WriteLine($"{(issues.Count == 0 ? "OK" : "DIVERGENCIA")} {release.ConjuntoId} {release.Id}: {release.Partitions.Count} particao(es)");
            foreach (var issue in issues) Console.WriteLine("  - " + issue);
            problems += issues.Count;
        }

        if (checkAll)
        {
            await using var cmd = new NpgsqlCommand("SELECT publication_id FROM arquivo.publicacao ORDER BY publication_id", pg);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                if (!active.ContainsKey(id))
                {
                    Console.WriteLine($"DIVERGENCIA publicacao {id} existe no banco, mas nao esta ativa no manifesto.");
                    problems++;
                }
            }
        }

        Console.WriteLine($"Checagem concluida: publicacoes={selected.Count}; particoes verificadas={checkedPartitions}; divergencias={problems}.");
        Console.WriteLine("A checagem valida manifesto, Parquet e metadados do schema arquivo. Ela nao compara conteudo ja carregado no PostgreSQL.");
        return problems == 0 ? 0 : 2;
    }

    private static async Task CheckDatabaseRegistrationAsync(NpgsqlConnection pg, LoaderConfig config,
        ManifestRelease release, List<string> issues, Action partitionChecked)
    {
        if (release.Partitions.Count == 0)
        {
            issues.Add("manifesto sem particoes.");
            return;
        }
        var columns = release.Partitions[0].Columns;
        if (release.Partitions.Any(x => !x.Columns.SequenceEqual(columns, StringComparer.Ordinal)))
            issues.Add("particoes da publicacao possuem schemas diferentes.");

        string? tableName = null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name, columns, definition_version FROM arquivo.conjunto WHERE conjunto_id=@id AND definition_version=@version", pg))
        {
            cmd.Parameters.AddWithValue("id", release.ConjuntoId);
            cmd.Parameters.AddWithValue("version", release.VersaoDefinicao);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                tableName = reader.GetString(0);
                if (!reader.GetFieldValue<string[]>(1).SequenceEqual(columns, StringComparer.Ordinal))
                    issues.Add("colunas registradas diferem do manifesto.");
                if (reader.GetInt32(2) != release.VersaoDefinicao)
                    issues.Add($"versao de schema registrada={reader.GetInt32(2)} diverge do manifesto={release.VersaoDefinicao}.");
            }
            else issues.Add("conjunto nao cadastrado em arquivo.conjunto.");
        }

        await using (var cmd = new NpgsqlCommand(
            "SELECT conjunto_id, definition_version FROM arquivo.publicacao WHERE publication_id=@id", pg))
        {
            cmd.Parameters.AddWithValue("id", release.Id);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) issues.Add("publicacao ausente em arquivo.publicacao.");
            else
            {
                if (reader.GetString(0) != release.ConjuntoId) issues.Add("publicacao vinculada a outro conjunto.");
                if (reader.GetInt32(1) != release.VersaoDefinicao) issues.Add("versao de definicao divergente em arquivo.publicacao.");
            }
        }

        if (tableName is not null)
        {
            var actual = await ReadPhysicalColumnsAsync(pg, tableName);
            var expected = new[] { (Name: "publicacao_id", Type: "text"), (Name: "particao_id", Type: "text"), (Name: "_linha", Type: "bigint") }
                .Concat(columns.Select(x => (Name: x, Type: "text"))).ToArray();
            if (!actual.SequenceEqual(expected)) issues.Add("colunas ou tipos fisicos da tabela divergem do schema do conjunto.");
        }

        var expectedPartIds = release.Partitions.Select(p => p.ArquivoId).ToHashSet(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT partition_id FROM arquivo.particao WHERE publication_id=@id", pg))
        {
            cmd.Parameters.AddWithValue("id", release.Id);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                if (!expectedPartIds.Contains(id)) issues.Add($"particao excedente no banco: {id}.");
            }
        }

        foreach (var part in release.Partitions)
        {
            var relativePath = BuildRelativeParquetPath(release, part);
            try
            {
                var actual = InspectParquet(ResolveParquetPath(config.ParquetRoot, relativePath), part.Columns, config.DuckDb);
                if (actual.Rows != part.Rows) issues.Add($"{part.ArquivoId}: linhas Parquet={actual.Rows}, manifesto={part.Rows}.");
            }
            catch (Exception ex) { issues.Add($"{part.ArquivoId}: Parquet: {ex.Message}"); }

            await using var cmd = new NpgsqlCommand("""
                SELECT conjunto_id, definition_version, relative_path, expected_rows, source_sha256
                FROM arquivo.particao WHERE publication_id=@publication_id AND partition_id=@partition_id
                """, pg);
            cmd.Parameters.AddWithValue("publication_id", release.Id);
            cmd.Parameters.AddWithValue("partition_id", part.ArquivoId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) issues.Add($"particao ausente no banco: {part.ArquivoId}.");
            else
            {
                if (reader.GetString(0) != release.ConjuntoId) issues.Add($"{part.ArquivoId}: conjunto divergente no banco.");
                if (reader.GetInt32(1) != release.VersaoDefinicao) issues.Add($"{part.ArquivoId}: versao divergente no banco.");
                if (reader.GetString(2) != relativePath) issues.Add($"{part.ArquivoId}: caminho divergente no banco.");
                if (reader.GetInt64(3) != part.Rows) issues.Add($"{part.ArquivoId}: expected_rows diverge do catalogo.");
                if (reader.IsDBNull(4) || !string.Equals(reader.GetString(4), part.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    issues.Add($"{part.ArquivoId}: source_sha256 ausente ou diferente do catalogo.");
            }
            partitionChecked();
        }
    }

    private static async Task<(string Name, string Type)[]> ReadPhysicalColumnsAsync(NpgsqlConnection pg, string tableName)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_schema='arquivo' AND table_name=@table ORDER BY ordinal_position
            """, pg);
        cmd.Parameters.AddWithValue("table", tableName);
        var columns = new List<(string Name, string Type)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add((reader.GetString(0), reader.GetString(1)));
        return columns.ToArray();
    }

    private static async Task<PublicationManifest> LoadPublicationStateAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<PublicationManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("publications-v3.json vazio ou invalido.");
        if (state.Releases.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != state.Releases.Count)
            throw new InvalidDataException("Manifesto contem IDs de publicacao duplicados.");
        return state;
    }

    private static ParquetInfo InspectParquet(string path, string[] expectedColumns, DuckDbConfig config)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Arquivo Parquet nao encontrado.", path);
        if (expectedColumns.Length == 0 || expectedColumns.Any(x => !SafeIdentifier.IsMatch(x)))
            throw new InvalidDataException("Manifesto contem nomes de coluna invalidos.");
        Directory.CreateDirectory(config.TempDirectory);
        using var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        ExecuteDuck(duck, $"SET threads={config.Threads.ToString(CultureInfo.InvariantCulture)}");
        ExecuteDuck(duck, $"SET memory_limit={DuckLiteral(config.MemoryLimit)}");
        ExecuteDuck(duck, $"SET temp_directory={DuckLiteral(Path.GetFullPath(config.TempDirectory))}");
        var literal = DuckLiteral(Path.GetFullPath(path));
        var actualColumns = new List<string>();
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = $"SELECT * FROM read_parquet({literal}) LIMIT 0";
            using var reader = cmd.ExecuteReader();
            for (var i = 0; i < reader.FieldCount; i++) actualColumns.Add(reader.GetName(i));
        }
        var expected = new[] { "_linha" }.Concat(expectedColumns).ToArray();
        if (!actualColumns.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Esquema Parquet difere das colunas publicadas no manifesto.");
        using var count = duck.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM read_parquet({literal})";
        var rows = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new ParquetInfo(rows, actualColumns.ToArray());
    }

    private static string BuildRelativeParquetPath(ManifestRelease release, ManifestPartition part)
    {
        static bool Unsafe(string value) => string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.Contains('/') || value.Contains('\\');
        if (Unsafe(release.Id) || Unsafe(part.ArquivoId) || Path.IsPathRooted(part.ParquetFile) ||
            part.ParquetFile.Split(['/', '\\']).Any(Unsafe))
            throw new InvalidDataException("Caminho de Parquet inseguro no manifesto.");
        return $"{release.Id}/{part.ArquivoId}/{part.ParquetFile.Replace('\\', '/') }";
    }

    private static string ResolveParquetPath(string rootPath, string relativePath)
    {
        var root = Path.GetFullPath(rootPath);
        var segments = relativePath.Split('/');
        var full = Path.GetFullPath(Path.Combine(segments.Prepend(root).ToArray()));
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Caminho Parquet saiu de parquetRoot.");
        return full;
    }

    private sealed class PublicationManifest { public List<ManifestRelease> Releases { get; set; } = []; }
    private sealed class ManifestRelease
    {
        public string Id { get; set; } = "";
        public string ConjuntoId { get; set; } = "";
        public int VersaoDefinicao { get; set; }
        public DateTimeOffset? PurgedAt { get; set; }
        public List<ManifestPartition> Partitions { get; set; } = [];
    }
    private sealed class ManifestPartition
    {
        public string ArquivoId { get; set; } = "";
        public string ParquetFile { get; set; } = "";
        public long Rows { get; set; }
        public string OriginalSha256 { get; set; } = "";
        public string[] Columns { get; set; } = [];
    }
    private sealed record RegistrationPartition(string Id, string RelativePath, long Rows, string SourceSha256, string[] Columns);
    private sealed record ParquetInfo(long Rows, string[] Columns);
}

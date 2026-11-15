using System.Text.Json;
using DuckDB.NET.Data;

// Somente metadados do rodapé; nenhum COUNT(*) ou leitura de registros.
if (args.Length is < 1 or > 2 || (args.Length == 2 && (!int.TryParse(args[1], out var n) || n < 1 || n > 100)))
{
    Console.Error.WriteLine("Uso: ParquetEvidence DIRETORIO_DO_LOTE [MAX_ARQUIVOS=3; máximo 100]");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var limit = args.Length == 2 ? int.Parse(args[1]) : 3;
var results = new List<object>();
var errors = new List<string>();
long expectedTotal = 0, inspectedTotal = 0;
int eligible = 0, inspected = 0;
try
{
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "organization-run.json")));
    if (manifest.RootElement.GetProperty("status").GetString() != "READY")
        throw new InvalidDataException("O lote não está READY.");
    using var duck = new DuckDBConnection("DataSource=:memory:");
    duck.Open();
    using (var setup = duck.CreateCommand())
    {
        setup.CommandText = "SET threads=1; SET memory_limit='128MB'; SET temp_directory=''; SET autoinstall_known_extensions=false; SET autoload_known_extensions=false";
        setup.ExecuteNonQuery();
    }
    var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    foreach (var pub in manifest.RootElement.GetProperty("publications").EnumerateArray())
    foreach (var part in pub.GetProperty("partitions").EnumerateArray())
    {
        var relative = part.GetProperty("parquet").GetString() ?? throw new InvalidDataException("Caminho vazio.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var local = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || local == ".." || local.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidDataException("Parquet fora do lote.");
        if (path.IndexOfAny(['*', '?', '[', ']']) >= 0)
            throw new InvalidDataException("Caminho com padrão de busca não permitido.");
        if (!seen.Add(path)) throw new InvalidDataException("Parquet repetido no manifesto.");
        var expected = part.GetProperty("rows").GetInt64();
        expectedTotal = checked(expectedTotal + expected);
        eligible++;
        if (inspected >= limit) continue;
        inspected++;
        try
        {
            var before = new FileInfo(path);
            var size = before.Length;
            var modified = before.LastWriteTimeUtc;
            using var cmd = duck.CreateCommand();
            cmd.CommandText = "SELECT num_rows, num_row_groups FROM parquet_file_metadata('" + path.Replace("'", "''") + "')";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeout.Token.Register(() => { try { cmd.Cancel(); } catch (ObjectDisposedException) { } });
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("Rodapé ausente.");
            var rows = Convert.ToInt64(reader.GetValue(0));
            var groups = Convert.ToInt64(reader.GetValue(1));
            var after = new FileInfo(path);
            if (after.Length != size || after.LastWriteTimeUtc != modified)
                throw new IOException("Arquivo alterado durante a coleta; repetir com lote estável.");
            inspectedTotal = checked(inspectedTotal + rows);
            results.Add(new { arquivo = relative, linhas_manifesto = expected, linhas_rodape = rows, grupos = groups, bytes = size, confere = rows == expected });
            if (rows != expected) errors.Add(relative + ": contagem diverge do manifesto.");
        }
        catch (Exception ex) { errors.Add(relative + ": " + ex.Message); }
    }
}
catch (Exception ex) { errors.Add(ex.Message); }
Console.WriteLine(JsonSerializer.Serialize(new {
    coletado_em = DateTimeOffset.UtcNow, lote = root, limite = limit,
    arquivos_no_manifesto = eligible, arquivos_tentados = inspected,
    linhas_declaradas_manifesto = expectedTotal, linhas_rodapes_lidos = inspectedTotal,
    cobertura_integral = eligible > 0 && inspected == eligible && errors.Count == 0,
    metodo = "Metadados Parquet; não verifica conteúdo, fingerprints nem PostgreSQL.",
    arquivos = results, erros = errors
}, new JsonSerializerOptions { WriteIndented = true }));
return errors.Count == 0 ? 0 : 1;

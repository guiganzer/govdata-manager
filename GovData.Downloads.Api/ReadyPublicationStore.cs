using System.Text.Json;
using System.Text.RegularExpressions;
using GovData.Downloads.Core;
using GovData.Downloads.Server;

namespace GovData.Downloads.Api;

public sealed record ApiPublication(string RunDirectory, PublishedRelease Release);

/// <summary>Lê apenas lotes explícitos, concluídos e bloqueados contra reexecução pelo organizador.</summary>
public sealed class ReadyPublicationStore : IDisposable
{
    private readonly List<FileStream> _locks = [];
    private readonly Dictionary<(string, string), string> _paths = [];
    public List<ApiPublication> Publications { get; } = [];
    public Catalogo Catalog { get; private set; } = new();
    public ConjuntoDefinition[] Definitions { get; private set; } = [];

    public static async Task<ReadyPublicationStore> OpenAsync(ApiOptions options)
    {
        var store = new ReadyPublicationStore();
        try
        {
            if (!File.Exists(options.CatalogPath)) throw new FileNotFoundException("Catálogo da API não encontrado.");
            store.Catalog = await new CatalogStore(options.CatalogPath).LoadAsync();
            store.Definitions = Directory.EnumerateFiles(options.DefinitionsPath, "*.json")
                .Order(StringComparer.Ordinal).Select(ConjuntoContract.Read).ToArray();
            foreach (var directory in options.RunDirectories)
                await store.LoadRunAsync(Path.GetFullPath(directory));
            if (store.Publications.Select(x => x.Release.Id).Distinct().Count() != store.Publications.Count)
                throw new InvalidDataException("A mesma publicação está configurada em mais de um lote.");
            return store;
        }
        catch { store.Dispose(); throw; }
    }

    private async Task LoadRunAsync(string root)
    {
        // O organizador usa FileShare.None neste mesmo arquivo. Não criar estado do produtor pela API.
        _locks.Add(new FileStream(Path.Combine(root, "publication-state.owner"), FileMode.Open,
            FileAccess.Read, FileShare.Read));
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "organization-run.json")));
        var manifest = run.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 1 || manifest.GetProperty("status").GetString() != "READY")
            throw new InvalidDataException("API aceita somente lotes READY.");
        var ids = manifest.GetProperty("publicationIds").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || ids.Any(x => !ValidHash(x)))
            throw new InvalidDataException("IDs de publicação inválidos no lote.");
        var details = manifest.GetProperty("publications").EnumerateArray().ToArray();
        if (!details.Select(x => x.GetProperty("id").GetString()).Order().SequenceEqual(ids.Order()))
            throw new InvalidDataException("Detalhes não correspondem à lista de publicações.");
        var state = JsonSerializer.Deserialize<PublicationState>(
            await File.ReadAllTextAsync(Path.Combine(root, "publications-v3.json")), JsonStorage.Options)
            ?? throw new InvalidDataException("Estado de publicações vazio.");
        if (state.Releases.Select(x => x.Id).Distinct().Count() != state.Releases.Count)
            throw new InvalidDataException("Estado contém publicações duplicadas.");
        foreach (var detail in details)
        {
            var id = detail.GetProperty("id").GetString()!;
            var release = state.Releases.SingleOrDefault(x => x.Id == id && x.PurgedAt is null)
                ?? throw new InvalidDataException("Publicação ausente ou expurgada.");
            if (!SafeId(release.ConjuntoId) || detail.GetProperty("conjuntoId").GetString() != release.ConjuntoId ||
                detail.GetProperty("versaoDefinicao").GetInt32() != release.VersaoDefinicao)
                throw new InvalidDataException("Conjunto/versão divergente no lote.");
            var parts = detail.GetProperty("partitions").EnumerateArray().ToArray();
            if (release.Partitions.Count == 0 || release.Partitions.Select(x => x.ArquivoId).Distinct().Count() != release.Partitions.Count ||
                !parts.Select(x => x.GetProperty("id").GetString()).Order().SequenceEqual(release.Partitions.Select(x => x.ArquivoId).Order()))
                throw new InvalidDataException("Partições divergentes no lote.");
            foreach (var part in release.Partitions)
            {
                if (!SafeId(part.ArquivoId) || !ValidHash(part.OriginalSha256) || part.Rows < 0 ||
                    part.Columns.Count == 0 || part.Columns.Contains("_linha") ||
                    part.Columns.Distinct(StringComparer.Ordinal).Count() != part.Columns.Count)
                    throw new InvalidDataException("Metadados da partição inválidos.");
                var item = parts.Single(x => x.GetProperty("id").GetString() == part.ArquivoId);
                var expected = $"releases/{id}/{part.ArquivoId}/{part.ParquetFile.Replace('\\', '/')}";
                if (item.GetProperty("rows").GetInt64() != part.Rows ||
                    item.GetProperty("originalSha256").GetString() != part.OriginalSha256 ||
                    item.GetProperty("parquet").GetString()?.Replace('\\', '/') != expected)
                    throw new InvalidDataException("Contagem, hash ou caminho divergente no lote.");
                var path = ContainedFile(root, expected);
                InspectParquet(path, part);
                _paths.Add((id, part.ArquivoId), path);
            }
            Publications.Add(new(root, release));
        }
    }

    private static void InspectParquet(string path, PublishedPartition part)
    {
        using var connection = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        connection.Open();
        DuckSql.Exec(connection, "SET threads=1; SET memory_limit='128MB'; SET temp_directory=''; " +
            "SET autoinstall_known_extensions=false; SET autoload_known_extensions=false");
        DuckSql.Exec(connection, $"SET allowed_paths=[{DuckSql.Lit(path)}]; SET enable_external_access=false; SET lock_configuration=true");
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM read_parquet({DuckSql.Lit(path)}) LIMIT 0";
        using (var reader = command.ExecuteReader())
        {
            var actual = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName);
            if (!actual.SequenceEqual(new[] { "_linha" }.Concat(part.Columns), StringComparer.Ordinal))
                throw new InvalidDataException("Colunas Parquet divergem do manifesto.");
        }
        // COUNT sem filtro sobre Parquet usa metadados; não é conferência integral de conteúdo.
        command.CommandText = $"SELECT count(*) FROM read_parquet({DuckSql.Lit(path)})";
        if (Convert.ToInt64(command.ExecuteScalar()) != part.Rows)
            throw new InvalidDataException("Contagem Parquet diverge do manifesto.");
    }

    private static bool ValidHash(string value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$");
    private static bool SafeId(string value) => Regex.IsMatch(value, "^[a-z0-9][a-z0-9._-]{0,255}$");

    private static string ContainedFile(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(x => x is ".." or "." or ""))
            throw new InvalidDataException("Caminho de publicação inválido.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison) || !File.Exists(path))
            throw new InvalidDataException("Parquet ausente ou fora do lote.");
        var walk = root;
        foreach (var component in relative.Split('/', '\\'))
        {
            walk = Path.Combine(walk, component);
            if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links dentro de publicações não são aceitos pela API.");
        }
        return path;
    }

    public string ParquetPath(PublishedRelease release, PublishedPartition part) => _paths[(release.Id, part.ArquivoId)];
    public void Dispose() { foreach (var handle in _locks) handle.Dispose(); _locks.Clear(); }
}

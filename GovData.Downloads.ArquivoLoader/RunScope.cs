using System.Text.Json;
using System.Text.RegularExpressions;

namespace GovData.Downloads.ArquivoLoader;

internal static partial class Program
{
    private sealed record RunScope(string Directory, string ReleasesPath, string PublicationStatePath, string[] PublicationIds);

    private static async Task<RunScope> LoadRunScopeAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        var runPath = Path.Combine(root, "organization-run.json");
        var statePath = Path.Combine(root, "publications-v3.json");
        var releasesPath = Path.Combine(root, "releases");
        if (!File.Exists(runPath) || !File.Exists(statePath) || !Directory.Exists(releasesPath))
            throw new InvalidDataException("A pasta deve conter organization-run.json, publications-v3.json e releases/.");
        await using var input = File.OpenRead(runPath);
        using var document = await JsonDocument.ParseAsync(input);
        var run = document.RootElement;
        if (run.GetProperty("schemaVersion").GetInt32() != 1 ||
            run.GetProperty("status").GetString() != "READY")
            throw new InvalidDataException("A organização não terminou com status READY.");
        var ids = run.GetProperty("publicationIds").EnumerateArray()
            .Select(x => x.GetString() ?? "").ToArray();
        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            ids.Any(x => !Regex.IsMatch(x, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Lista de publicações vazia, duplicada ou inválida.");
        var state = await LoadPublicationStateAsync(statePath);
        var active = state.Releases.Where(x => x.PurgedAt is null).Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Any(x => !active.Contains(x)))
            throw new InvalidDataException("Uma publicação do lote não existe ou foi expurgada no manifesto.");
        foreach (var item in run.GetProperty("publications").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (id is null || !ids.Contains(id, StringComparer.Ordinal))
                throw new InvalidDataException("A lista de publicações diverge dos detalhes do lote.");
            var release = state.Releases.Single(x => x.Id == id);
            if (item.GetProperty("conjuntoId").GetString() != release.ConjuntoId ||
                item.GetProperty("versaoDefinicao").GetInt32() != release.VersaoDefinicao)
                throw new InvalidDataException($"Conjunto ou versão divergente no lote: {id}.");
            var runParts = item.GetProperty("partitions").EnumerateArray().ToArray();
            var parts = runParts.Select(x => x.GetProperty("id").GetString()).ToArray();
            if (!parts.Order(StringComparer.Ordinal).SequenceEqual(
                release.Partitions.Select(x => x.ArquivoId).Order(StringComparer.Ordinal)))
                throw new InvalidDataException($"Partições divergentes no lote: {id}.");
            foreach (var runPart in runParts)
            {
                var partId = runPart.GetProperty("id").GetString();
                var manifestPart = release.Partitions.Single(x => x.ArquivoId == partId);
                var expectedPath = $"releases/{release.Id}/{partId}/{manifestPart.ParquetFile.Replace('\\', '/')}";
                if (runPart.GetProperty("rows").GetInt64() != manifestPart.Rows ||
                    !string.Equals(runPart.GetProperty("originalSha256").GetString(),
                        manifestPart.OriginalSha256, StringComparison.OrdinalIgnoreCase) ||
                    runPart.GetProperty("parquet").GetString()!.Replace('\\', '/') != expectedPath)
                    throw new InvalidDataException($"Linhas, SHA ou caminho divergente em {id}/{partId}.");
            }
        }
        if (run.GetProperty("publications").GetArrayLength() != ids.Length)
            throw new InvalidDataException("Número de publicações diverge no lote.");
        return new RunScope(root, releasesPath, statePath, ids);
    }
}

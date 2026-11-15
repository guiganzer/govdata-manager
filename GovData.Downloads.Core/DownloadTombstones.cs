using System.Text.Json;

namespace GovData.Downloads.Core;

public sealed class DownloadTombstone
{
    public string ArquivoId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Destino { get; set; } = "";
    public string? Competencia { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; }
}

public sealed class DownloadTombstoneStore(string path)
{
    public async Task<List<DownloadTombstone>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<DownloadTombstone>>(stream, JsonStorage.Options, ct) ?? [];
    }

    public async Task UpsertAsync(DownloadTombstone item, CancellationToken ct = default)
    {
        var list = await LoadAsync(ct);
        var previous = list.FirstOrDefault(x => x.Destino.Equals(item.Destino, StringComparison.OrdinalIgnoreCase));
        if (previous is not null && previous.ArquivoId == item.ArquivoId &&
            previous.Sha256 == item.Sha256 && previous.Url == item.Url &&
            previous.Competencia == item.Competencia && previous.CheckedAt >= item.CheckedAt) return;
        if (previous is not null && previous.Sha256 == item.Sha256 && previous.Url == item.Url &&
            previous.CheckedAt > item.CheckedAt) item.CheckedAt = previous.CheckedAt;
        list.RemoveAll(x => x.Destino.Equals(item.Destino, StringComparison.OrdinalIgnoreCase));
        list.Add(item);
        await JsonStorage.SaveAsync(path, list, ct);
    }
}

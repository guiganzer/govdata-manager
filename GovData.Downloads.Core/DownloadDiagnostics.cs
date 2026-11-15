using System.Text.Json;

namespace GovData.Downloads.Core;

public sealed record DownloadEvent(
    DateTimeOffset EmUtc,
    Guid? DownloadId,
    string Nivel,
    string Codigo,
    string Mensagem,
    string? Detalhe = null);

public sealed record DownloadIssue(DownloadEvent Evento, string? Destino);

public sealed class DownloadDiagnostics(string eventsPath)
{
    private const long MaxLogBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions LineOptions = new(JsonStorage.Options) { WriteIndented = false };
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public string EventsPath { get; } = eventsPath;
    public string? LastWriteError { get; private set; }
    public string? LastReadError { get; private set; }
    public event Action<DownloadEvent>? EventRecorded;

    public async Task LogAsync(Guid? jobId, string level, string code, string message, string? detail = null)
    {
        var entry = new DownloadEvent(DateTimeOffset.UtcNow, jobId, level, code, message, detail);
        await _writeGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EventsPath)!);
            if (File.Exists(EventsPath) && new FileInfo(EventsPath).Length >= MaxLogBytes)
            {
                // Keep every segment: acquisition failures are part of the audit trail.
                var archive = EventsPath + "." + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
                File.Move(EventsPath, archive);
            }
            await File.AppendAllTextAsync(EventsPath, JsonSerializer.Serialize(entry, LineOptions) + Environment.NewLine);
            LastWriteError = null;
            EventRecorded?.Invoke(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastWriteError = ex.Message;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<DownloadEvent>> ReadRecentAsync(Guid? jobId = null, int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _writeGate.WaitAsync();
        try
        {
            var result = await Task.Run<IReadOnlyList<DownloadEvent>>(() =>
            {
                var recent = new Queue<DownloadEvent>();
                foreach (var path in EventFiles())
                {
                    foreach (var line in File.ReadLines(path))
                    {
                        try
                        {
                            var item = JsonSerializer.Deserialize<DownloadEvent>(line, JsonStorage.Options);
                            if (item is null || (jobId is not null && item.DownloadId != jobId)) continue;
                            recent.Enqueue(item);
                            if (recent.Count > limit) recent.Dequeue();
                        }
                        catch (JsonException) { /* Ignore incomplete lines. */ }
                    }
                }
                return recent.Reverse().ToArray();
            });
            LastReadError = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastReadError = ex.Message;
            return [];
        }
        finally { _writeGate.Release(); }
    }

    public Task<IReadOnlyList<DownloadEvent>> ReadRecentForJobAsync(DownloadJob job, int limit = 200)
        => ReadRelatedAsync(job, limit);

    public async Task<IReadOnlyList<DownloadIssue>> ReadRecentIssuesAsync(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _writeGate.WaitAsync();
        try
        {
            var result = await Task.Run<IReadOnlyList<DownloadIssue>>(() =>
            {
                var destinations = new Dictionary<Guid, string>();
                var recent = new Queue<DownloadIssue>();
                foreach (var path in EventFiles())
                    foreach (var item in ReadLines(path))
                    {
                        if (item.Codigo == "job.enqueued" && item.DownloadId is Guid id &&
                            EnqueuedDestination(item.Detalhe) is { } target)
                            destinations[id] = target;
                        if (item.Nivel is not ("error" or "warn")) continue;
                        var targetForEvent = item.DownloadId is Guid eventId && destinations.TryGetValue(eventId, out var found)
                            ? found : null;
                        recent.Enqueue(new DownloadIssue(item, targetForEvent));
                        if (recent.Count > limit) recent.Dequeue();
                    }
                return recent.Reverse().ToArray();
            });
            LastReadError = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastReadError = ex.Message;
            return [];
        }
        finally { _writeGate.Release(); }
    }

    private async Task<IReadOnlyList<DownloadEvent>> ReadRelatedAsync(DownloadJob job, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _writeGate.WaitAsync();
        try
        {
            var result = await Task.Run<IReadOnlyList<DownloadEvent>>(() =>
            {
                var paths = EventFiles();
                var related = new HashSet<Guid> { job.Id };
                foreach (var path in paths)
                    foreach (var item in ReadLines(path))
                        if (item.Codigo == "job.enqueued" && item.DownloadId is Guid id &&
                            string.Equals(EnqueuedDestination(item.Detalhe), job.Destino,
                                StringComparison.OrdinalIgnoreCase))
                            related.Add(id);
                var recent = new Queue<DownloadEvent>();
                foreach (var path in paths)
                    foreach (var item in ReadLines(path))
                        if (item.DownloadId is Guid id && related.Contains(id))
                        {
                            recent.Enqueue(item);
                            if (recent.Count > limit) recent.Dequeue();
                        }
                return recent.Reverse().ToArray();
            });
            LastReadError = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastReadError = ex.Message;
            return [];
        }
        finally { _writeGate.Release(); }
    }

    private string[] EventFiles()
    {
        var directory = Path.GetDirectoryName(EventsPath)!;
        if (!Directory.Exists(directory)) return [];
        var name = Path.GetFileName(EventsPath);
        return Directory.EnumerateFiles(directory, name + ".*")
            .Where(path =>
            {
                var filename = Path.GetFileName(path);
                if (!filename.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)) return false;
                if (filename.Equals(name + ".1", StringComparison.OrdinalIgnoreCase)) return true;
                return DateTimeOffset.TryParseExact(filename.AsSpan((name + ".").Length),
                    "yyyyMMdd-HHmmss-fffffff", null, System.Globalization.DateTimeStyles.None, out _);
            })
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .Concat(File.Exists(EventsPath) ? [EventsPath] : [])
            .ToArray();
    }

    private static IEnumerable<DownloadEvent> ReadLines(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            DownloadEvent? item;
            try { item = JsonSerializer.Deserialize<DownloadEvent>(line, JsonStorage.Options); }
            catch (JsonException) { continue; }
            if (item is not null) yield return item;
        }
    }

    private static string? EnqueuedDestination(string? detail)
    {
        const string start = "destino: ";
        const string end = "; competência:";
        if (detail is null) return null;
        var from = detail.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (from < 0) return null;
        from += start.Length;
        var to = detail.IndexOf(end, from, StringComparison.OrdinalIgnoreCase);
        return to > from ? detail[from..to] : null;
    }

    public async Task<string> ExportReportAsync(DownloadJob job)
    {
        var folder = Path.Combine(Path.GetDirectoryName(EventsPath)!, "reports");
        var path = Path.Combine(folder, $"diagnostico-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{job.Id:N}.json");
        var events = await ReadRecentForJobAsync(job, 500);
        await JsonStorage.SaveAsync(path, new
        {
            GeradoEmUtc = DateTimeOffset.UtcNow,
            Download = job,
            Eventos = events,
            ErroDeLog = LastWriteError,
            ErroDeLeitura = LastReadError
        });
        return path;
    }
}

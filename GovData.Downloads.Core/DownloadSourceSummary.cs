namespace GovData.Downloads.Core;

public sealed record DownloadSourceSummary(
    int CatalogSources,
    int Queued,
    int Downloading,
    int Completed,
    int Attention,
    int WithoutExecution,
    int HistoricalExecutions,
    int OutsideCatalogExecutions,
    int TotalExecutions)
{
    public static DownloadSourceSummary From(IEnumerable<string> sourceIds, IEnumerable<DownloadJob> jobs)
    {
        var catalogIds = sourceIds.ToHashSet(StringComparer.Ordinal);
        var jobList = jobs as IReadOnlyList<DownloadJob> ?? jobs.ToArray();
        var latest = LatestBySource(catalogIds, jobList);
        var totalExecutions = 0;
        var catalogExecutions = 0;
        foreach (var job in jobList)
        {
            totalExecutions++;
            if (catalogIds.Contains(job.ArquivoId)) catalogExecutions++;
        }
        var current = latest.Values;

        return new DownloadSourceSummary(
            catalogIds.Count,
            current.Count(job => job.Status == DownloadStatus.NaFila),
            current.Count(job => job.Status == DownloadStatus.Baixando),
            current.Count(job => job.Status == DownloadStatus.Concluido),
            current.Count(job => job.Status is DownloadStatus.Pausado or DownloadStatus.Falhou or DownloadStatus.Obsoleto),
            catalogIds.Count - latest.Count,
            catalogExecutions - latest.Count,
            totalExecutions - catalogExecutions,
            totalExecutions);
    }

    public static IReadOnlyList<DownloadJob> CurrentJobs(IEnumerable<string> sourceIds, IEnumerable<DownloadJob> jobs) =>
        LatestBySource(sourceIds.ToHashSet(StringComparer.Ordinal), jobs).Values
            .OrderByDescending(job => job.CriadoEmUtc).ThenByDescending(job => job.Id).ToArray();

    private static Dictionary<string, DownloadJob> LatestBySource(HashSet<string> catalogIds, IEnumerable<DownloadJob> jobs)
    {
        var latest = new Dictionary<string, DownloadJob>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (!catalogIds.Contains(job.ArquivoId)) continue;
            if (!latest.TryGetValue(job.ArquivoId, out var previous)
                || job.CriadoEmUtc > previous.CriadoEmUtc
                || (job.CriadoEmUtc == previous.CriadoEmUtc && job.Id.CompareTo(previous.Id) > 0))
                latest[job.ArquivoId] = job;
        }
        return latest;
    }
}

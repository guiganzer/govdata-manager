using System.Text.RegularExpressions;

namespace GovData.Downloads.Server;

public sealed class ServerOptions
{
    public string? CatalogPath { get; set; }
    public string? ConjuntosPath { get; set; }
    public string? DownloadRoot { get; set; }
    public string? StorageRoot { get; set; }
    public long MaxMaterializedBytes { get; set; } = 500L * 1024 * 1024 * 1024;
    public int CaptureBatchRows { get; set; } = 4096;
    public bool RecaptureGrid { get; set; }
    public int DuckDbThreads { get; set; } = 1;
    public int DuckDbMemoryMb { get; set; }
    // CSV (solto ou em ZIP) a partir deste tamanho descompactado usa o leitor nativo do DuckDB.
    // 0 desativa o modo massivo.
    public int MassiveCsvThresholdMb { get; set; } = 256;
    public string[] AutomaticPublicationConjuntos { get; set; } = [];

    public void ValidateProcessingOptions()
    {
        if (CaptureBatchRows is < 100 or > 65536 || DuckDbThreads is < 1 or > 32 ||
            (DuckDbMemoryMb != 0 && (DuckDbMemoryMb < 512 || DuckDbMemoryMb > 65536)) ||
            MassiveCsvThresholdMb is < 0 or > 1_048_576)
            throw new InvalidDataException("Lote, threads, memória DuckDB ou limite do modo massivo fora do intervalo permitido.");
        if (AutomaticPublicationConjuntos is null || AutomaticPublicationConjuntos.Length > 64 ||
            AutomaticPublicationConjuntos.Any(pattern => string.IsNullOrWhiteSpace(pattern) ||
                pattern.Length > 80 || pattern != "*" &&
                !Regex.IsMatch(pattern, "^[a-z0-9][a-z0-9._*-]{0,79}$", RegexOptions.CultureInvariant)) ||
            AutomaticPublicationConjuntos.Distinct(StringComparer.Ordinal).Count() != AutomaticPublicationConjuntos.Length)
            throw new InvalidDataException("automaticPublicationConjuntos deve conter no máximo 64 IDs/padrões de conjunto válidos e sem duplicatas.");
    }

    public bool AllowsAutomaticPublication(string conjuntoId) =>
        AutomaticPublicationConjuntos is { Length: > 0 } patterns &&
        ConjuntoContract.Matches(conjuntoId, patterns);

}


public sealed class PublicationPlan
{
    public List<PublicationDefinition> Publications { get; set; } = [];
}

public sealed record PublicationCoverage(
    int VerifiedDownloads, int CoveredDownloads, int OutsideManifest,
    string[] OutsideManifestNames, int Documents = 0);

public sealed class PublicationDefinition
{
    public string Id { get; set; } = "";
    public string ConjuntoId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PartitionDefinition> Partitions { get; set; } = [];
    public ConjuntoDefinition Definition { get; set; } = new();
}

public sealed class PartitionDefinition
{
    public string ArquivoId { get; set; } = "";
    public string Format { get; set; } = "";
}

public sealed class PublicationState
{
    public List<PublishedRelease> Releases { get; set; } = [];
    public List<PublicationFailure> Failures { get; set; } = [];
    public List<PublishTiming> Timings { get; set; } = [];
}

public sealed class PublicationFailure
{
    public string DefinitionId { get; set; } = "";
    public string InputSignature { get; set; } = "";
    public DateTimeOffset NextAttemptAt { get; set; }
    public string Message { get; set; } = "";
}

public sealed class PublishedRelease
{
    public int VersaoDefinicao { get; set; }
    public string? BasePublicationId { get; set; }
    public string Id { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string ConjuntoId { get; set; } = "";
    public string DefinitionHash { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset? BronzeConfirmedAt { get; set; }
    public string? BronzeExecutionId { get; set; }
    public BronzeConfirmation? BronzeProof { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
    public List<PublishedPartition> Partitions { get; set; } = [];
}

public sealed class PublishedPartition
{
    public string ParquetFile { get; set; } = "";
    public QualityReport Validations { get; set; } = new();
    public string ArquivoId { get; set; } = "";
    public string OriginalSha256 { get; set; } = "";
    public long OriginalBytes { get; set; }
    public DateTimeOffset SourceModifiedAt { get; set; }
    public DateTimeOffset SourceVerifiedAt { get; set; }
    public string SourcePath { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string? Competencia { get; set; }
    public long Rows { get; set; }
    public List<string> Columns { get; set; } = [];
    public string SchemaHash { get; set; } = "";
    public string CoverageSha256 { get; set; } = "";
    public string OriginalFile { get; set; } = "";
    public List<PublishedSegment> Segments { get; set; } = [];
}

public sealed class PublishedSegment
{
    public long Start { get; set; }
    public long Rows { get; set; }
    public long DataBytes { get; set; }
    public long IndexBytes { get; set; }
}

public sealed class BronzeConfirmation
{
    public string PublicationId { get; set; } = "";
    public int EndpointId { get; set; }
    public long LoteId { get; set; }
    public List<BronzePartitionConfirmation> Partitions { get; set; } = [];
}

public sealed class BronzePartitionConfirmation
{
    public string ArquivoId { get; set; } = "";
    public string OriginalSha256 { get; set; } = "";
    public long Rows { get; set; }
    public long FirstLine { get; set; }
    public long LastLine { get; set; }
    public long PageCount { get; set; }
    public string CoverageSha256 { get; set; } = "";
    public long WorkUnitId { get; set; }
    public string ScopeHash { get; set; } = "";
    public long CurrentExecutionId { get; set; }
    public List<long> OriginExecutionIds { get; set; } = [];
}

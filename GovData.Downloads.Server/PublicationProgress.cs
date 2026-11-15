namespace GovData.Downloads.Server;

/// <summary>Duração de uma publicação concluída; base das estimativas de término.</summary>
public sealed class PublishTiming
{
    public string DefinitionId { get; set; } = "";
    public string ConjuntoId { get; set; } = "";
    public long SourceBytes { get; set; }
    public double Seconds { get; set; }
    public long Rows { get; set; }
    public int Columns { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
}

public sealed record ProgressCurrent(string DefinitionId, string ConjuntoId, string ArquivoId, int PartitionNumber,
    int PartitionCount, string Stage, long StageDone, long StageTotal, double ElapsedSeconds, double StageSeconds,
    long SourceBytes, double? EstimatedSeconds);

/// <summary>Status: AGUARDANDO (pronta para publicar), AGUARDANDO_DOWNLOAD, FALHA, PUBLICADA.</summary>
public sealed record ProgressItem(string DefinitionId, string ConjuntoId, string ArquivoId, string Status,
    long SourceBytes, double? EstimatedSeconds, string? Message = null);

public sealed record PublicationProgressSnapshot(int Total, int Published, int InProgress, int Waiting,
    int WaitingDownload, int Failed, bool CycleRunning, DateTimeOffset? CycleStartedAt,
    double? EstimatedRemainingSeconds, DateTimeOffset? EstimatedFinishAt, bool EstimateIncomplete,
    ProgressCurrent? Current, ProgressItem[] Queue, int QueueHidden);

/// <summary>Estado em memória da publicação em curso (uma edição por vez), atualizado pelo motor e lido pela API.</summary>
public sealed class PublicationProgress(TimeProvider clock)
{
    private readonly object _gate = new();
    private string? _definitionId, _conjuntoId, _arquivoId, _stage;
    private int _partition, _partitions;
    private long _done, _total, _sourceBytes;
    private DateTimeOffset _started, _stageStarted;
    private DateTimeOffset? _cycleStarted;
    private bool _cycleRunning;

    public readonly record struct Current(string DefinitionId, string ConjuntoId, string ArquivoId, int Partition,
        int Partitions, string Stage, long Done, long Total, DateTimeOffset Started, DateTimeOffset StageStarted,
        long SourceBytes);

    public void CycleStarted() { lock (_gate) { _cycleRunning = true; _cycleStarted = clock.GetUtcNow(); } }
    public void CycleFinished() { lock (_gate) _cycleRunning = false; }

    public (bool Running, DateTimeOffset? Started) Cycle() { lock (_gate) return (_cycleRunning, _cycleStarted); }

    public void Begin(string definitionId, string conjuntoId, string arquivoId, int partitions, long sourceBytes)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            _definitionId = definitionId; _conjuntoId = conjuntoId; _arquivoId = arquivoId;
            _partition = 1; _partitions = partitions; _sourceBytes = sourceBytes;
            _stage = "Iniciando"; _done = 0; _total = 0; _started = now; _stageStarted = now;
        }
    }

    public void Partition(int number, string arquivoId)
    {
        lock (_gate) { _partition = number; _arquivoId = arquivoId; }
    }

    /// <summary>Muda ou atualiza a etapa; o cronômetro da etapa só reinicia quando o nome muda.</summary>
    public void Stage(string stage, long done = 0, long total = 0)
    {
        lock (_gate)
        {
            if (_definitionId is null) return;
            if (_stage != stage) { _stage = stage; _stageStarted = clock.GetUtcNow(); }
            _done = done; _total = total;
        }
    }

    public void End()
    {
        lock (_gate) { _definitionId = null; _stage = null; }
    }

    public Current? Get()
    {
        lock (_gate)
            return _definitionId is null ? null : new Current(_definitionId, _conjuntoId!, _arquivoId!, _partition,
                _partitions, _stage ?? "", _done, _total, _started, _stageStarted, _sourceBytes);
    }
}

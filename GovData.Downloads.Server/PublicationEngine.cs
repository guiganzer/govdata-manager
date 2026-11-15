using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GovData.Downloads.Core;

namespace GovData.Downloads.Server;

public sealed class PublicationEngine
{
    private readonly ServerOptions _options;
    private readonly bool _includeApiCompatibilityArtifacts;
    private readonly TimeProvider _clock;
    private readonly CatalogStore _catalog;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _manifestLoadGate = new(1, 1);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly SemaphoreSlim _auditGate = new(1, 1);
    private readonly object _readersLock = new();
    private readonly Dictionary<string, int> _readers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recapturedGrids = new(StringComparer.Ordinal);
    private readonly string _statePath;
    private readonly string _auditPath;
    private PublicationState _state = new();
    private PublicationPlan _manifest = new();
    public string? LastError { get; private set; }
    public PublicationProgress Progress { get; }

    public PublicationEngine(ServerOptions options, TimeProvider? clock = null,
        bool includeApiCompatibilityArtifacts = true)
    {
        _options = options;
        _includeApiCompatibilityArtifacts = includeApiCompatibilityArtifacts;
        _clock = clock ?? TimeProvider.System;
        Progress = new PublicationProgress(_clock);
        _catalog = new CatalogStore(options.CatalogPath!);
        _statePath = Path.Combine(options.StorageRoot!, "publications-v3.json");
        _auditPath = Path.Combine(options.StorageRoot!, "audit.jsonl");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StorageRoot!);
        if (File.Exists(_statePath))
        {
            await using var stream = File.OpenRead(_statePath);
            _state = await JsonSerializer.DeserializeAsync<PublicationState>(stream, JsonStorage.Options, cancellationToken)
                ?? throw new InvalidDataException("Estado de publicações vazio.");
        }
        LastError = _state.Failures.LastOrDefault() is { } recent
            ? $"{recent.DefinitionId}: {recent.Message}" : null;
        foreach (var release in _state.Releases.Where(x => x.PurgedAt is null))
            foreach (var part in release.Partitions)
            {
                if (part.ParquetFile.Length == 0 ||
                    !File.Exists(Path.Combine(_options.StorageRoot!, "releases", release.Id, part.ArquivoId, part.ParquetFile)))
                    throw new InvalidDataException($"Publicação {release.Id} incompleta no armazenamento local.");
                long next = 0;
                for (var i = 0; i < part.Segments.Count; i++)
                {
                    var segment = part.Segments[i];
                    var dataPath = SegmentDataPath(release, part, i);
                    var indexPath = SegmentIndexPath(release, part, i);
                    if (segment.Start != next || segment.Rows < 0 ||
                        segment.IndexBytes != checked(segment.Rows * 8) ||
                        segment.DataBytes < 0 || !File.Exists(dataPath) || !File.Exists(indexPath) ||
                        new FileInfo(dataPath).Length != segment.DataBytes ||
                        new FileInfo(indexPath).Length != segment.IndexBytes)
                        throw new InvalidDataException($"Segmento {i} da publicação {release.Id} incompleto.");
                    next += segment.Rows;
                }
                if (part.Segments.Count > 0 && next != part.Rows)
                    throw new InvalidDataException($"Contagem de segmentos da publicação {release.Id} incorreta.");
            }
        await LoadManifestAsync(cancellationToken);
    }

    public async Task LoadManifestAsync(CancellationToken cancellationToken)
    {
        await _manifestLoadGate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await _catalog.LoadAsync(cancellationToken);
            var manual = new PublicationPlan();
            Directory.CreateDirectory(_options.ConjuntosPath!);
            var definitionFiles = Directory.EnumerateFiles(_options.ConjuntosPath!, "*.json").Order(StringComparer.Ordinal).ToArray();
            var all = definitionFiles.Select(ConjuntoContract.Read).ToArray();
            var duplicated = all.Select((x, i) => (x.Conjunto, File: Path.GetFileName(definitionFiles[i]))).GroupBy(x => x.Conjunto)
                .Where(g => g.Count() > 1).ToArray();
            if (duplicated.Length > 0)
                throw new InvalidDataException("Conjuntos duplicados: " + string.Join("; ", duplicated.Select(g =>
                    $"{g.Key} em {string.Join(", ", g.Select(x => x.File))}")) +
                    ". Mantenha um único arquivo por conjunto; cópias de segurança devem terminar em .bak.");
            var remaining = all.ToList();
            while (remaining.Count > 0)
            {
                var definition = remaining.FirstOrDefault(x => x.Tipo == "ORGANIZADO" || manual.Publications.Any(p => p.ConjuntoId == x.Base));
                if (definition is null) throw new InvalidDataException("Base ausente ou ciclo entre conjuntos derivados.");
                remaining.Remove(definition);
                if (definition.Tipo == "DERIVADO")
                {
                    foreach (var source in manual.Publications.Where(p => p.ConjuntoId == definition.Base).ToArray())
                        manual.Publications.Add(new PublicationDefinition { Id=definition.Conjunto+":"+source.Partitions[0].ArquivoId,
                            ConjuntoId=definition.Conjunto,Name=definition.Nome,Definition=definition,Partitions=source.Partitions });
                    continue;
                }
                var functions = catalog.Orgaos.SelectMany(o => o.Funcoes).Where(f => f.Id == definition.Fontes!.Funcao).ToArray();
                if (functions.Length == 0) continue;
                if (functions.Length != 1) throw new InvalidDataException("Função ambígua: " + definition.Fontes!.Funcao);
                var files = functions[0].Arquivos.Where(f => ConjuntoContract.Matches(f.Id,definition.Fontes!.Arquivos) &&
                    !f.NomeArquivo.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase)).OrderBy(f=>f.Id,StringComparer.Ordinal).ToArray();
                if (files.Length == 0) continue;
                if (definition.EdicaoPor == "LISTA" && definition.Fontes!.Arquivos.Any(p => p.Contains('*')))
                    throw new InvalidDataException("LISTA exige IDs exatos de todos os arquivos.");
                if (definition.EdicaoPor == "LISTA" && files.Length != definition.Fontes!.Arquivos.Length)
                    throw new InvalidDataException("LISTA contém arquivos inexistentes.");
                var groups = definition.EdicaoPor == "ARQUIVO" ? files.Select(f=>new[]{f}) : new[]{files};
                foreach (var group in groups)
                    manual.Publications.Add(new PublicationDefinition { Id=definition.Conjunto+":"+group[0].Id,
                        ConjuntoId=definition.Conjunto,Name=definition.Nome,Definition=definition,
                        Partitions=group.Select(f=>new PartitionDefinition { ArquivoId=f.Id,Format=Path.GetExtension(f.NomeArquivo).TrimStart('.').ToLowerInvariant() }).ToList() });
            }
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _manifest = manual;
            }
            finally { _gate.Release(); }
        }
        finally { _manifestLoadGate.Release(); }
    }

    public async Task<(PublicationDefinition[] Definitions, PublishedRelease[] Releases)> SnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (_manifest.Publications.ToArray(), _state.Releases.ToArray()); }
        finally { _gate.Release(); }
    }

    public async Task<PublicationCoverage> CoverageAsync(CancellationToken ct)
    {
        var catalog = await _catalog.LoadAsync(ct);
        var receipts = await ReadReceiptsAsync(ct);
        var definitions = (await SnapshotAsync(ct)).Definitions;
        var configured = definitions.SelectMany(definition => definition.Partitions)
            .Select(partition => partition.ArquivoId).ToHashSet(StringComparer.Ordinal);
        var verified = 0;
        var covered = 0;
        var documents = 0;
        var outside = new List<string>();
        foreach (var source in catalog.Orgaos.SelectMany(org => org.Funcoes.SelectMany(func =>
                     func.Arquivos.Select(file => (Org: org, Func: func, File: file)))))
        {
            var path = DownloadPaths.ForFile(_options.DownloadRoot!, source.Org, source.Func, source.File);
            if (!receipts.TryGetValue(path, out var receipt) ||
                !ReceiptMatches(receipt, source.File, path)) continue;
            try { EnsurePlainSourcePath(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { continue; }
            verified++;
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                documents++;
                continue;
            }
            if (configured.Contains(source.File.Id)) covered++;
            else outside.Add(source.File.Nome);
        }
        return new PublicationCoverage(verified, covered, outside.Count,
            outside.OrderBy(name => name, StringComparer.CurrentCulture).Take(8).ToArray(), documents);
    }

    public async Task<PublicationFailure[]> FailuresAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return _state.Failures.ToArray(); }
        finally { _gate.Release(); }
    }

    private static double? Estimate(PublishTiming[] timings, string conjuntoId, long bytes)
    {
        if (bytes <= 0) return null;
        var peers = timings.Where(t => t.ConjuntoId == conjuntoId && t.SourceBytes > 0).ToArray();
        if (peers.Length == 0) peers = timings.Where(t => t.SourceBytes > 0).ToArray();
        if (peers.Length == 0) return null;
        return bytes * (peers.Sum(t => t.Seconds) / peers.Sum(t => (double)t.SourceBytes));
    }

    /// <summary>
    /// Andamento da publicação: edição em curso (etapa e avanço), fila na ordem de processamento e estimativa de término.
    /// A estimativa usa as durações já medidas (segundos por byte de origem, do mesmo conjunto quando houver); sem
    /// nenhuma edição concluída ainda, não há estimativa.
    /// </summary>
    public async Task<PublicationProgressSnapshot> ProgressAsync(CancellationToken ct)
    {
        var catalog = await _catalog.LoadAsync(ct);
        PublicationPlan manifest;
        PublishedRelease[] releases;
        PublicationFailure[] failures;
        PublishTiming[] timings;
        await _gate.WaitAsync(ct);
        try
        {
            manifest = _manifest; releases = _state.Releases.ToArray();
            failures = _state.Failures.ToArray(); timings = _state.Timings.ToArray();
        }
        finally { _gate.Release(); }
        var lookup = catalog.Orgaos.SelectMany(o => o.Funcoes.SelectMany(f => f.Arquivos
            .Select(a => (Org: o, Func: f, File: a)))).ToDictionary(x => x.File.Id, StringComparer.Ordinal);
        var receipts = await ReadReceiptsAsync(ct);
        var current = Progress.Get();
        var eligible = manifest.Publications.Where(d => _options.AllowsAutomaticPublication(d.ConjuntoId)).ToArray();
        var queue = new List<ProgressItem>();
        int published = 0, waiting = 0, waitingDownload = 0, failed = 0;
        foreach (var definition in eligible)
        {
            if (current is { } running && running.DefinitionId == definition.Id) continue;
            if (releases.Any(r => r.DefinitionId == definition.Id)) { published++; continue; }
            long bytes = 0;
            var ready = true;
            foreach (var part in definition.Partitions)
            {
                if (!lookup.TryGetValue(part.ArquivoId, out var source)) { ready = false; break; }
                var path = DownloadPaths.ForFile(_options.DownloadRoot!, source.Org, source.Func, source.File);
                if (!receipts.TryGetValue(path, out var receipt) || !ReceiptMatches(receipt, source.File, path))
                { ready = false; break; }
                bytes += receipt.TamanhoLocalBytes;
            }
            var arquivo = definition.Partitions.FirstOrDefault()?.ArquivoId ?? "";
            if (failures.FirstOrDefault(x => x.DefinitionId == definition.Id) is { } failure)
            {
                failed++;
                queue.Add(new ProgressItem(definition.Id, definition.ConjuntoId, arquivo, "FALHA", bytes, null, failure.Message));
            }
            else if (!ready)
            {
                waitingDownload++;
                queue.Add(new ProgressItem(definition.Id, definition.ConjuntoId, arquivo, "AGUARDANDO_DOWNLOAD", bytes, null));
            }
            else
            {
                waiting++;
                queue.Add(new ProgressItem(definition.Id, definition.ConjuntoId, arquivo, "AGUARDANDO", bytes,
                    Estimate(timings, definition.ConjuntoId, bytes)));
            }
        }

        var now = _clock.GetUtcNow();
        double remaining = 0;
        var incomplete = false;
        ProgressCurrent? currentView = null;
        if (current is { } cur)
        {
            var estimate = Estimate(timings, cur.ConjuntoId, cur.SourceBytes);
            var elapsedSeconds = (now - cur.Started).TotalSeconds;
            currentView = new ProgressCurrent(cur.DefinitionId, cur.ConjuntoId, cur.ArquivoId, cur.Partition, cur.Partitions,
                cur.Stage, cur.Done, cur.Total, elapsedSeconds, (now - cur.StageStarted).TotalSeconds, cur.SourceBytes, estimate);
            if (estimate is { } whole) remaining += Math.Max(0, whole - elapsedSeconds);
            else incomplete = true;
        }
        foreach (var item in queue.Where(x => x.Status == "AGUARDANDO"))
            if (item.EstimatedSeconds is { } seconds) remaining += seconds;
            else incomplete = true;
        var hasWork = current is not null || waiting > 0;
        double? estimatedRemaining = hasWork && (remaining > 0 || !incomplete) ? remaining : null;
        var cycle = Progress.Cycle();
        const int visible = 40;
        return new PublicationProgressSnapshot(eligible.Length, published, current is null ? 0 : 1, waiting,
            waitingDownload, failed, cycle.Running, cycle.Started, estimatedRemaining,
            estimatedRemaining is { } left ? now.AddSeconds(left) : null, hasWork && incomplete, currentView,
            queue.Take(visible).ToArray(), Math.Max(0, queue.Count - visible));
    }

    public Task ReconcileAsync(CancellationToken cancellationToken, bool force = false) =>
        ReconcileCoreAsync(cancellationToken, force, sourceFileIds: null);

    /// <summary>Organizes configured definitions sourced by one catalog agency, without reconciling other agencies.</summary>
    public async Task<PublishedRelease[]> ReconcileAgencyAsync(string orgaoId, CancellationToken cancellationToken,
        bool force = false, bool recheckExisting = false)
    {
        if (string.IsNullOrWhiteSpace(orgaoId)) throw new ArgumentException("Informe o ID do órgão.", nameof(orgaoId));
        var catalog = await _catalog.LoadAsync(cancellationToken);
        var agency = catalog.Orgaos.SingleOrDefault(o =>
            string.Equals(o.Id, orgaoId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Órgão '{orgaoId}' não existe no catálogo v2.");
        var sourceFileIds = agency.Funcoes.SelectMany(f => f.Arquivos).Select(f => f.Id)
            .ToHashSet(StringComparer.Ordinal);
        await ReconcileCoreAsync(cancellationToken, force, sourceFileIds, recheckExisting);

        var after = await SnapshotAsync(cancellationToken);
        var relevantDefinitions = after.Definitions
            .Where(d => _options.AllowsAutomaticPublication(d.ConjuntoId) &&
                d.Partitions.Any(p => sourceFileIds.Contains(p.ArquivoId)))
            .Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        return after.Releases.Where(r => r.PurgedAt is null && relevantDefinitions.Contains(r.DefinitionId)).ToArray();
    }

    /// <summary>Organizes only definitions sourced by one function of one catalog agency.</summary>
    public async Task<PublishedRelease[]> ReconcileFunctionAsync(string orgaoId, string funcaoId,
        CancellationToken cancellationToken, bool force = false, bool recheckExisting = false)
    {
        var catalog = await _catalog.LoadAsync(cancellationToken);
        var agency = catalog.Orgaos.SingleOrDefault(o =>
            string.Equals(o.Id, orgaoId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Órgão '{orgaoId}' não existe no catálogo v2.");
        var function = agency.Funcoes.SingleOrDefault(f =>
            string.Equals(f.Id, funcaoId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Função '{funcaoId}' não existe no órgão '{orgaoId}'.");
        var sourceFileIds = function.Arquivos.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        await ReconcileCoreAsync(cancellationToken, force, sourceFileIds, recheckExisting);
        var after = await SnapshotAsync(cancellationToken);
        var relevantDefinitions = after.Definitions
            .Where(d => _options.AllowsAutomaticPublication(d.ConjuntoId) &&
                d.Partitions.Any(p => sourceFileIds.Contains(p.ArquivoId)))
            .Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        return after.Releases.Where(r => r.PurgedAt is null && relevantDefinitions.Contains(r.DefinitionId)).ToArray();
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken, bool force,
        IReadOnlySet<string>? sourceFileIds, bool recheckExisting = false)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        Progress.CycleStarted();
        try
        {
            await LoadManifestAsync(cancellationToken);
            if (_options.AutomaticPublicationConjuntos is not { Length: > 0 })
                return;
            var catalog = await _catalog.LoadAsync(cancellationToken);
            PublicationPlan manifest;
            PublishedRelease[] known;
            PublicationFailure[] failed;
            await _gate.WaitAsync(cancellationToken);
            try { manifest = _manifest; known = _state.Releases.ToArray(); failed = _state.Failures.ToArray(); }
            finally { _gate.Release(); }

            var lookup = catalog.Orgaos.SelectMany(o => o.Funcoes.SelectMany(f => f.Arquivos
                .Select(a => (Org: o, Func: f, File: a)))).ToDictionary(x => x.File.Id, StringComparer.Ordinal);
            var receipts = await ReadReceiptsAsync(cancellationToken);
            string? cycleError = null;
            var relevantDefinitionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in manifest.Publications)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_options.AllowsAutomaticPublication(definition.ConjuntoId)) continue;
                if (sourceFileIds is not null && !definition.Partitions.Any(p => sourceFileIds.Contains(p.ArquivoId))) continue;
                relevantDefinitionIds.Add(definition.Id);
                known = (await SnapshotAsync(cancellationToken)).Releases;
                // No ciclo automático, uma publicação existente encerra a elegibilidade dessa edição,
                // mesmo se o parser, a definição ou o arquivo de origem ganhar uma assinatura nova.
                // Reconciliação manual ainda pode criar uma edição imutável atualizada.
                if (!force && !recheckExisting && known.Any(x => x.DefinitionId == definition.Id)) continue;
                string? inputSignature = null;
                try
                {
                    var sources = new List<(PartitionDefinition Part, string Path, DownloadReceipt Receipt)>();
                    var ready = true;
                    foreach (var part in definition.Partitions)
                    {
                        var source = lookup[part.ArquivoId];
                        var path = DownloadPaths.ForFile(_options.DownloadRoot!, source.Org, source.Func, source.File);
                        if (File.Exists(path)) EnsurePlainSourcePath(path);
                        if (!receipts.TryGetValue(path, out var receipt) || !ReceiptMatches(receipt, source.File, path))
                        { ready = false; break; }
                        sources.Add((part, path, receipt));
                    }
                    if (!ready) continue;
                    var referenceHashes = definition.Definition.Referencias.OrderBy(p=>p.Key).Select(p=>
                        p.Key+":"+Hash(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(_options.CatalogPath!)!,p.Value)))).ToArray();
                    var parent = definition.Definition.Tipo == "DERIVADO" ? known.LastOrDefault(r=>r.ConjuntoId==definition.Definition.Base && r.PurgedAt is null &&
                        r.Partitions.Select(p=>p.ArquivoId).Order().SequenceEqual(definition.Partitions.Select(p=>p.ArquivoId).Order()) &&
                        sources.All(s => r.Partitions.Any(p => p.ArquivoId == s.Part.ArquivoId &&
                            p.OriginalBytes == s.Receipt.TamanhoLocalBytes && p.SourceModifiedAt == s.Receipt.ModificadoEmUtc))) : null;
                    if (definition.Definition.Tipo == "DERIVADO" && parent is null) continue;
                    var definitionHash = Hash(ConjuntoContract.CanonicalJson(new
                    { motor=ConjuntoOrganizer.Version, grade=RawGrid.Version, definition.Definition, referenceHashes, parent=parent?.Id }));
                    inputSignature = Hash(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        definitionHash,
                        processing = new
                        {
                            duckDbMemory = DuckSql.MemoryLimit(_options.DuckDbMemoryMb),
                            _options.DuckDbThreads,
                            _options.CaptureBatchRows,
                            _options.MassiveCsvThresholdMb
                        },
                        sources = sources.Select(x => new { x.Part.ArquivoId,
                            x.Receipt.Url, x.Receipt.TamanhoLocalBytes,
                            x.Receipt.ModificadoEmUtc, x.Receipt.ETag })
                    }));
                    // Uma falha de entrada idêntica não é repetida em cada ciclo. Mudança na assinatura
                    // habilita uma nova tentativa; a ação manual também pode solicitar outra tentativa.
                    if (!force && failed.Any(x => x.DefinitionId == definition.Id &&
                        x.InputSignature == inputSignature))
                        continue;
                    var current = known.LastOrDefault(x => x.DefinitionId == definition.Id);
                    if (current is not null && current.DefinitionHash == definitionHash &&
                        current.Partitions.Count == sources.Count && sources.All(x =>
                            current.Partitions.Any(p => p.ArquivoId == x.Part.ArquivoId &&
                                p.OriginalBytes == x.Receipt.TamanhoLocalBytes &&
                                p.SourceModifiedAt == x.Receipt.ModificadoEmUtc)))
                    {
                        await ClearFailureAsync(definition.Id, cancellationToken);
                        continue;
                    }
                    await PublishAsync(definition, definitionHash, sources, parent, cancellationToken);
                    await ClearFailureAsync(definition.Id, cancellationToken);
                }
                // Qualquer falha de uma edição (inclusive memória do DuckDB) fica registrada nela, com nova tentativa em
                // 30 min, e o ciclo segue para as demais; antes, a exceção abortava o ciclo e repetia a mesma edição.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    cycleError = LastError = $"{definition.Id}: {ex.Message}";
                    if (inputSignature is not null)
                        await RecordFailureAsync(definition.Id, inputSignature, ex.Message, cancellationToken);
                    await AuditAsync("publication.failed", definition.Id, ex.Message, cancellationToken);
                }
            }
            var remaining = (await FailuresAsync(cancellationToken))
                .Where(f => relevantDefinitionIds.Contains(f.DefinitionId)).ToArray();
            LastError = cycleError ?? (remaining.LastOrDefault() is { } recent
                ? $"{recent.DefinitionId}: {recent.Message}" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            throw;
        }
        finally { Progress.CycleFinished(); _reconcileGate.Release(); }
    }

    private async Task RecordFailureAsync(string id, string signature, string message, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _state.Failures.RemoveAll(x => x.DefinitionId == id);
            _state.Failures.Add(new PublicationFailure
            {
                DefinitionId = id, InputSignature = signature,
                NextAttemptAt = _clock.GetUtcNow().AddMinutes(30), Message = message
            });
            await SaveStateAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task ClearFailureAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state.Failures.RemoveAll(x => x.DefinitionId == id) > 0)
                await SaveStateAsync(ct);
            LastError = _state.Failures.LastOrDefault() is { } recent
                ? $"{recent.DefinitionId}: {recent.Message}" : null;
        }
        finally { _gate.Release(); }
    }

    private async Task PublishAsync(PublicationDefinition definition, string definitionHash,
        List<(PartitionDefinition Part, string Path, DownloadReceipt Receipt)> sources,
        PublishedRelease? parent, CancellationToken ct)
    {
        var parentRead = parent is null ? null : await AcquireAsync(parent.ConjuntoId,
            parent.Partitions[0].ArquivoId, parent.Id, ct);
        if (parent is not null && (parentRead is null || parentRead.Value.Release.PurgedAt is not null))
        {
            parentRead?.Lease.Dispose();
            throw new InvalidDataException("Publicação de base indisponível para derivação.");
        }
        using var parentLease = parentRead?.Lease;
        var staging = Path.Combine(_options.StorageRoot!, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var elapsed = Stopwatch.StartNew();
        var sourceBytes = sources.Sum(x => x.Receipt.TamanhoLocalBytes);
        try
        {
            Progress.Begin(definition.Id, definition.ConjuntoId, sources[0].Part.ArquivoId, sources.Count, sourceBytes);
            var parts = new List<PublishedPartition>();
            var number = 0;
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                Progress.Partition(++number, source.Part.ArquivoId);
                var partDir = Path.Combine(staging, source.Part.ArquivoId);
                Directory.CreateDirectory(partDir);
                var original = Path.Combine(partDir, "original");
                var capacity = StorageProtection.ReadCapacity(original);
                if (capacity.AvailableBytes - source.Receipt.TamanhoLocalBytes <= capacity.TotalBytes / 5)
                    throw new IOException("A publicação foi adiada para manter ao menos 20% do volume livre.");
                Progress.Stage("Copiando o original", 0, source.Receipt.TamanhoLocalBytes);
                var hash = await CopyAndHashAsync(source.Path, original, ct,
                    done => Progress.Stage("Copiando o original", done, source.Receipt.TamanhoLocalBytes));
                var size = new FileInfo(original).Length;
                if (size != source.Receipt.TamanhoLocalBytes ||
                    source.Receipt.Sha256Esperado is { } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                    !ReceiptMatches(source.Receipt, null, source.Path))
                    throw new InvalidDataException("Arquivo mudou durante a aquisição; tentativa de publicação cancelada.");
                var part = new PublishedPartition
                {
                    ArquivoId = source.Part.ArquivoId,
                    OriginalSha256 = hash,
                    OriginalBytes = size,
                    SourceModifiedAt = source.Receipt.ModificadoEmUtc,
                    SourceVerifiedAt = source.Receipt.VerificadoEmUtc,
                    SourcePath = source.Path,
                    SourceUrl = source.Receipt.Url,
                    Competencia = source.Receipt.Competencia,
                    OriginalFile = "original"
                };
                var d = definition.Definition;
                string grid;
                if (d.Tipo == "DERIVADO")
                {
                    var basePart = parent!.Partitions.Single(p=>p.ArquivoId==source.Part.ArquivoId);
                    if (basePart.OriginalSha256 != hash)
                        throw new InvalidDataException("Base derivada difere dos bytes atuais da origem.");
                    grid=Path.Combine(_options.StorageRoot!,"releases",parent.Id,basePart.ArquivoId,basePart.ParquetFile);
                }
                else
                {
                    var recapture = _options.RecaptureGrid && !_recapturedGrids.Contains(hash);
                    grid = await RawGrid.CaptureAsync(original,hash,source.Part.ArquivoId,source.Part.Format,d.Fontes!,
                        Path.Combine(_options.StorageRoot!,"grade"),ct,Progress.Stage,
                        _options.CaptureBatchRows, _options.DuckDbThreads, _options.DuckDbMemoryMb,
                        recapture, (long)_options.MassiveCsvThresholdMb << 20);
                    if (recapture) _recapturedGrids.Add(hash);
                }
                await ConjuntoOrganizer.MaterializeAsync(grid,source.Part.ArquivoId,d,partDir,
                    Path.GetDirectoryName(_options.CatalogPath!)!,part,ct,d.Tipo=="DERIVADO",
                    _options.DuckDbThreads,_options.DuckDbMemoryMb,Progress.Stage,
                    _includeApiCompatibilityArtifacts, _options.MaxMaterializedBytes);
                parts.Add(part);
            }
            if (parts.Count > 1 && definition.Definition.Validacoes.ChaveUnica)
            {
                Progress.Stage("Conferindo a chave entre partições");
                using var quality = DuckSql.Open(staging, threads: _options.DuckDbThreads,
                    memoryMegabytes: _options.DuckDbMemoryMb);
                var files = string.Join(',', parts.Select(p => DuckSql.Lit(Path.Combine(staging, p.ArquivoId, p.ParquetFile))));
                var keys = string.Join(',', definition.Definition.Chave.Select(DuckSql.Id));
                if (DuckSql.Count(quality, $"SELECT count(*) FROM (SELECT {keys} FROM read_parquet([{files}]) GROUP BY ALL HAVING count(*)>1)") > 0)
                    throw new InvalidDataException("Chave repetida entre partições da mesma publicação.");
            }
            var identity = ConjuntoOrganizer.Version + "|" + definitionHash + "|" + string.Join("|", parts.Select(x => x.ArquivoId + ":" + x.OriginalSha256));
            var releaseId = Hash(Encoding.UTF8.GetBytes(identity));
            var existing = _state.Releases.SingleOrDefault(x => x.Id == releaseId);
            if (existing is not null)
            {
                if (existing.PurgedAt is not null)
                {
                    var redundant = new PublishedRelease
                    {
                        Id = existing.Id, PurgedAt = existing.PurgedAt, Partitions = parts
                    };
                    await _cleanupGate.WaitAsync(ct);
                    try { await PurgeSourcesAsync([redundant], ct); }
                    finally { _cleanupGate.Release(); }
                }
                else
                {
                    await _gate.WaitAsync(ct);
                    try
                    {
                        foreach (var source in sources)
                        {
                            var prior = existing.Partitions.Single(p => p.ArquivoId == source.Part.ArquivoId);
                            prior.SourceModifiedAt = source.Receipt.ModificadoEmUtc;
                            prior.SourceVerifiedAt = source.Receipt.VerificadoEmUtc;
                            prior.SourcePath = source.Path;
                            prior.SourceUrl = source.Receipt.Url;
                            prior.Competencia = source.Receipt.Competencia;
                        }
                        await SaveStateAsync(ct);
                    }
                    finally { _gate.Release(); }
                }
                return;
            }
            Progress.Stage("Gravando a edição");
            var materializedBytes = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Sum(x => new FileInfo(x).Length);
            if (materializedBytes > _options.MaxMaterializedBytes)
                throw new IOException("Capacidade de armazenamento insuficiente para publicação segura.");
            var releasesDir = Path.Combine(_options.StorageRoot!, "releases");
            Directory.CreateDirectory(releasesDir);
            var final = Path.Combine(releasesDir, releaseId);
            if (Directory.Exists(final)) await DeleteDirectoryWithRetryAsync(final, ct); // orphan from an interrupted commit
            await MoveDirectoryWithRetryAsync(staging, final, ct);
            var release = new PublishedRelease
            {
                VersaoDefinicao = definition.Definition.VersaoDefinicao,
                BasePublicationId = parent?.Id,
                Id = releaseId,
                DefinitionId = definition.Id,
                ConjuntoId = definition.ConjuntoId,
                DefinitionHash = definitionHash,
                PublishedAt = _clock.GetUtcNow(),
                Partitions = parts
            };
            await _gate.WaitAsync(ct);
            try
            {
                _state.Releases.Add(release);
                _state.Timings.Add(new PublishTiming
                {
                    DefinitionId = definition.Id, ConjuntoId = definition.ConjuntoId, SourceBytes = sourceBytes,
                    Seconds = elapsed.Elapsed.TotalSeconds, Rows = parts.Sum(x => x.Rows),
                    Columns = parts.Max(x => x.Columns.Count), FinishedAt = _clock.GetUtcNow()
                });
                if (_state.Timings.Count > 300) _state.Timings.RemoveRange(0, _state.Timings.Count - 300);
                await SaveStateAsync(ct);
            }
            finally { _gate.Release(); }
            await AuditAsync("publication.published", releaseId,
                $"partitions={parts.Count};rows={parts.Sum(x => x.Rows)}", ct);
        }
        finally
        {
            Progress.End();
            if (Directory.Exists(staging)) await DeleteDirectoryWithRetryAsync(staging, CancellationToken.None);
        }
    }

    private async Task PurgeSourcesAsync(PublishedRelease[] purged, CancellationToken ct)
    {
        var tombstones = new DownloadTombstoneStore(Path.Combine(Path.GetDirectoryName(_options.CatalogPath!)!,
            "download-tombstones.json"));
        foreach (var (part, purgedAt) in purged
                     .SelectMany(x => x.Partitions.Select(p => (Part: p, PurgedAt: x.PurgedAt!.Value)))
                     .Where(x => x.Part.SourcePath.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            await _gate.WaitAsync(ct);
            try
            {
                if (SourceUsedByActiveRelease(part)) continue;
            }
            finally { _gate.Release(); }
            var receipts = await ReadReceiptsAsync(ct);
            if (!receipts.TryGetValue(part.SourcePath, out var receipt) ||
                receipt.ArquivoId != part.ArquivoId || receipt.Url != part.SourceUrl ||
                receipt.Competencia != part.Competencia || receipt.TamanhoLocalBytes != part.OriginalBytes ||
                receipt.ModificadoEmUtc != part.SourceModifiedAt) continue;
            var sourceExisted = File.Exists(part.SourcePath);
            if (sourceExisted)
            {
                EnsurePlainSourcePath(part.SourcePath);
                if (!ReceiptMatches(receipt, null, part.SourcePath)) continue;
                await using var input = File.OpenRead(part.SourcePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                if (hash != part.OriginalSha256) continue;
                input.Close();
            }
            await _gate.WaitAsync(ct);
            try
            {
                if (SourceUsedByActiveRelease(part)) continue;
                var currentReceipts = await ReadReceiptsAsync(ct);
                var existsNow = File.Exists(part.SourcePath);
                if (!currentReceipts.TryGetValue(part.SourcePath, out var current) || current != receipt ||
                    existsNow != sourceExisted ||
                    existsNow && !ReceiptMatches(receipt, null, part.SourcePath)) continue;
                if (existsNow)
                {
                    EnsurePlainSourcePath(part.SourcePath);
                    File.Delete(part.SourcePath);
                    await AuditAsync("source.deleted", part.ArquivoId, part.OriginalSha256, ct);
                }
                await tombstones.UpsertAsync(new DownloadTombstone
                {
                    ArquivoId = part.ArquivoId, Url = receipt.Url, Destino = part.SourcePath,
                    Competencia = part.Competencia, Sha256 = part.OriginalSha256,
                    CheckedAt = purgedAt
                }, ct);
            }
            finally { _gate.Release(); }
        }
    }

    private bool SourceUsedByActiveRelease(PublishedPartition part) =>
        _state.Releases.Any(r => r.PurgedAt is null && r.Partitions.Any(p =>
            p.SourcePath.Equals(part.SourcePath, StringComparison.OrdinalIgnoreCase) &&
            p.OriginalSha256 == part.OriginalSha256));

    private static async Task MoveDirectoryWithRetryAsync(string source, string target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(source, target); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
            { await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), ct); }
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
            { await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), ct); }
        }
    }

    public async Task<(PublishedRelease Release, PublishedPartition Part, IDisposable Lease)?> AcquireAsync(
        string conjuntoId, string partId, string? releaseId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var release = releaseId is null
                ? _state.Releases.LastOrDefault(x => x.ConjuntoId == conjuntoId && x.PurgedAt is null &&
                    x.Partitions.Any(p => p.ArquivoId == partId))
                : _state.Releases.SingleOrDefault(x => x.Id == releaseId && x.ConjuntoId == conjuntoId);
            if (release is null) return null;
            var part = release.Partitions.SingleOrDefault(x => x.ArquivoId == partId);
            if (part is null) return null;
            lock (_readersLock)
                _readers[release.Id] = _readers.GetValueOrDefault(release.Id) + 1;
            return (release, part, new ReaderLease(this, release.Id));
        }
        finally { _gate.Release(); }
    }

    private sealed class ReaderLease(PublicationEngine owner, string id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._readersLock)
            {
                var remaining = owner._readers[id] - 1;
                if (remaining == 0) owner._readers.Remove(id);
                else owner._readers[id] = remaining;
            }
        }
    }

    public string SegmentDataPath(PublishedRelease release, PublishedPartition part, int number) =>
        Path.Combine(_options.StorageRoot!, "releases", release.Id, part.ArquivoId, "segments",
            number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".rows.jsonl");
    public string SegmentIndexPath(PublishedRelease release, PublishedPartition part, int number) =>
        Path.Combine(_options.StorageRoot!, "releases", release.Id, part.ArquivoId, "segments",
            number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".rows.idx");

    private async Task<Dictionary<string, DownloadReceipt>> ReadReceiptsAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetDirectoryName(_options.CatalogPath!)!, "downloads-receipts.json");
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        await using var stream = File.OpenRead(path);
        var list = await JsonSerializer.DeserializeAsync<List<DownloadReceipt>>(stream, JsonStorage.Options, ct) ?? [];
        return list.ToDictionary(x => x.Destino, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ReceiptMatches(DownloadReceipt receipt, ArquivoCatalogo? file, string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == receipt.TamanhoLocalBytes &&
            info.LastWriteTimeUtc == receipt.ModificadoEmUtc.UtcDateTime &&
            (file is null || receipt.ArquivoId == file.Id && receipt.Url == file.Url &&
             receipt.Competencia == file.Competencia && receipt.Substituivel == file.Substituivel);
    }

    private void EnsurePlainSourcePath(string path)
    {
        var root = Path.GetFullPath(_options.DownloadRoot!);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Arquivo de origem fora da pasta de downloads.");
        var current = root;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Pasta de downloads com redirecionamento de sistema de arquivos.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Origem com link ou redirecionamento de sistema de arquivos.");
        }
    }

    private static async Task<string> CopyAndHashAsync(string source, string target, CancellationToken ct,
        Action<long>? copied = null)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        long writtenSinceCheck = 0, total = 0, reported = 0;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (writtenSinceCheck == 0 || writtenSinceCheck >= 8L * 1024 * 1024)
            {
                var capacity = StorageProtection.ReadCapacity(target);
                if (capacity.AvailableBytes - 8L * 1024 * 1024 <= capacity.TotalBytes / 5)
                    throw new IOException("A publicação foi interrompida para manter ao menos 20% do volume livre.");
                writtenSinceCheck = 0;
            }
            hash.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            writtenSinceCheck += count;
            total += count;
            if (copied is not null && total - reported >= 8L * 1024 * 1024) { reported = total; copied(total); }
        }
        output.Flush(true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task SaveStateAsync(CancellationToken ct) => await JsonStorage.SaveAsync(_statePath, _state, ct);
    private async Task AuditAsync(string eventName, string id, string detail, CancellationToken ct)
    {
        await _auditGate.WaitAsync(ct);
        try
        {
            var record = JsonSerializer.Serialize(new { at = _clock.GetUtcNow(), eventName, id, detail });
            await File.AppendAllTextAsync(_auditPath, record + "\n", ct);
        }
        finally { _auditGate.Release(); }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

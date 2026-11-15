using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using FluentFTP.Exceptions;

namespace GovData.Downloads.Core;

public enum DownloadStatus { NaFila, Baixando, Pausado, Concluido, Falhou, Obsoleto }

public sealed record DownloadReceipt(
    string ArquivoId, string Url, string Destino, string? Competencia, bool Substituivel,
    long? TamanhoEsperadoBytes, string? Sha256Esperado, DateTimeOffset VerificadoEmUtc,
    long TamanhoLocalBytes, DateTimeOffset ModificadoEmUtc, string? ETag)
{
    public DateTimeOffset? LastModifiedUtc { get; init; }
}

public sealed record DownloadJob(
    Guid Id,
    string ArquivoId,
    string Nome,
    string Url,
    string Destino,
    DownloadStatus Status,
    long BytesBaixados = 0,
    long? TotalBytes = null,
    string? Erro = null,
    string? ETag = null)
{
    public DateTimeOffset CriadoEmUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? IniciadoEmUtc { get; init; }
    public DateTimeOffset? FinalizadoEmUtc { get; init; }
    public DateTimeOffset? UltimoProgressoEmUtc { get; init; }
    public DateTimeOffset? UltimoErroEmUtc { get; init; }
    public int Tentativas { get; init; }
    public int? CodigoHttp { get; init; }
    public string? CategoriaErro { get; init; }
    public string? TipoErro { get; init; }
    public string? Integridade { get; init; }
    public long? TamanhoEsperadoBytes { get; init; }
    public string? Sha256Esperado { get; init; }
    public string? Sha256Calculado { get; init; }
    public string? Competencia { get; init; }
    public string? PaginaOrigem { get; init; }
    public bool Substituivel { get; init; }
    public bool Automatico { get; init; }
    public string? IfNoneMatch { get; init; }
    public bool PausaManual { get; init; }
    public bool PausaAutomacao { get; init; }
    public bool PausaArmazenamento { get; init; }
    public DateTimeOffset? VerificadoEmUtc { get; init; }
    public bool ForcarReinicio { get; init; }
    public bool PreservarAnterior { get; init; }
    public bool Prioritario { get; init; }
    public string? UltimaFalha { get; init; }
    public string? UltimaCategoriaFalha { get; init; }
    public DateTimeOffset? ProximaTentativaEmUtc { get; init; }
    public DateTimeOffset? LastModifiedUtc { get; init; }
}

public sealed class DownloadManager : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly DownloadOriginPolicy? _originPolicy;
    private readonly Func<string, StorageCapacity> _storageProbe;
    private readonly FtpDownloader _ftp = new();
    private readonly string _statePath;
    private readonly string _receiptsPath;
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, DownloadReceipt> _receipts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly object _queueLock = new();
    private readonly object _storageLock = new();
    private readonly LinkedList<Guid> _urgentQueue = new();
    private readonly LinkedList<Guid> _normalQueue = new();
    private readonly Dictionary<Guid, LinkedListNode<Guid>> _queued = new();
    private readonly HashSet<string> _activeHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _activeAgencies = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _agencyByFile = new(StringComparer.Ordinal);
    private Dictionary<string, int> _agencyLimits = new(StringComparer.OrdinalIgnoreCase);
    private int _defaultAgencyLimit = 2;
    private DateTime _agencyMapStamp;
    private readonly SemaphoreSlim _queueSignal = new(0, 1);
    private readonly ConcurrentDictionary<string, HostFailures> _hostFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _deferredHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _hostTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private readonly SemaphoreSlim _receiptGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _maxConcurrentDownloads = 3;
    private int _activeDownloads;
    private bool _initialized;
    private StorageProtectionState _storageState = StorageProtectionState.Unknown;
    private string? _blockedDestination;

    public const int MinConcurrentDownloads = 1;
    public const int MaxAllowedConcurrentDownloads = 16;
    public int MaxConcurrentDownloads => Volatile.Read(ref _maxConcurrentDownloads);
    public StorageProtectionState StorageState => Volatile.Read(ref _storageState);

    public void SetMaxConcurrentDownloads(int value)
    {
        if (value is < MinConcurrentDownloads or > MaxAllowedConcurrentDownloads)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"Informe entre {MinConcurrentDownloads} e {MaxAllowedConcurrentDownloads} downloads simultâneos.");
        Volatile.Write(ref _maxConcurrentDownloads, value);
        SignalQueue();
        Changed?.Invoke();
    }

    /// <summary>
    /// Limite de downloads simultâneos por órgão do catálogo: <paramref name="padrao"/> vale para todo órgão
    /// sem limite próprio em <paramref name="porOrgao"/>. Soma-se às regras de um download por servidor de
    /// origem e do limite global.
    /// </summary>
    public void SetAgencyLimits(int padrao, IReadOnlyDictionary<string, int>? porOrgao = null)
    {
        static void Check(int value, string what)
        {
            if (value is < MinConcurrentDownloads or > MaxAllowedConcurrentDownloads)
                throw new ArgumentOutOfRangeException(what,
                    $"Informe entre {MinConcurrentDownloads} e {MaxAllowedConcurrentDownloads} downloads simultâneos por órgão ({what}).");
        }
        Check(padrao, "maxDownloadsPorOrgao");
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (orgao, limite) in porOrgao ?? new Dictionary<string, int>())
        {
            if (string.IsNullOrWhiteSpace(orgao)) throw new ArgumentException("Órgão sem identificador em limitesPorOrgao.");
            Check(limite, orgao);
            limits[orgao.Trim()] = limite;
        }
        lock (_queueLock)
        {
            _defaultAgencyLimit = padrao;
            _agencyLimits = limits;
        }
        SignalQueue();
        Changed?.Invoke();
    }

    public int AgencyLimit(string orgaoId)
    {
        lock (_queueLock) return _agencyLimits.TryGetValue(orgaoId, out var l) ? l : _defaultAgencyLimit;
    }

    /// <summary>Órgão do catálogo dono do arquivo; sem catálogo, o servidor de origem faz as vezes de órgão.</summary>
    private string AgencyOf(DownloadJob job) =>
        _agencyByFile.TryGetValue(job.ArquivoId, out var orgao) ? orgao : "servidor:" + new Uri(job.Url).Host;

    private async Task LoadAgencyMapAsync()
    {
        var catalogPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "catalogo.json");
        if (!File.Exists(catalogPath)) return;
        // Relê só quando o catálogo muda: arquivo enfileirado fora do catálogo não força nova leitura.
        var stamp = File.GetLastWriteTimeUtc(catalogPath);
        if (stamp == _agencyMapStamp) return;
        _agencyMapStamp = stamp;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var orgao in (await new CatalogStore(catalogPath).LoadAsync()).Orgaos)
                foreach (var funcao in orgao.Funcoes)
                    foreach (var arquivo in funcao.Arquivos)
                        map[arquivo.Id] = orgao.Id;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // Catálogo ilegível não para a fila: mantém o mapa anterior e, para arquivos sem órgão
            // conhecido, o servidor de origem faz as vezes de órgão.
            await Diagnostics.LogAsync(null, "warn", "limits.catalog_unreadable",
                "Catálogo ilegível para o limite por órgão; usando o servidor de origem como agrupamento.", ex.Message);
            return;
        }
        lock (_queueLock) _agencyByFile = map;
    }

    public event Action? Changed;
    public event Action<StorageProtectionState>? StorageChanged;
    public IReadOnlyList<DownloadJob> Jobs => _jobs.Values.OrderByDescending(j => j.CriadoEmUtc).ToArray();
    public DownloadReceipt? ReceiptFor(string targetPath) => _receipts.TryGetValue(targetPath, out var receipt) ? receipt : null;
    public DownloadDiagnostics Diagnostics { get; }

    public DownloadManager(HttpClient http, string statePath) : this(http, statePath, TimeProvider.System) { }

    public DownloadManager(HttpClient http, string statePath, TimeProvider clock)
        : this(http, statePath, clock, null) { }

    public DownloadManager(HttpClient http, string statePath, TimeProvider clock, DownloadOriginPolicy? originPolicy,
        Func<string, StorageCapacity>? storageProbe = null)
    {
        _http = http;
        _clock = clock;
        _originPolicy = originPolicy;
        _storageProbe = storageProbe ?? StorageProtection.ReadCapacity;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _statePath = statePath;
        _receiptsPath = Path.Combine(Path.GetDirectoryName(statePath)!, "downloads-receipts.json");
        Diagnostics = new DownloadDiagnostics(Path.Combine(Path.GetDirectoryName(statePath)!, "eventos.jsonl"));
    }

    public async Task InitializeAsync(bool resumeAutomatic = true, bool resumePending = true,
        Func<DownloadJob, bool>? resumeFilter = null)
    {
        await _initGate.WaitAsync();
        try
        {
            if (_initialized) return;
            var settingsPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "settings.json");
            if (File.Exists(settingsPath))
            {
                await using var settingsFile = File.OpenRead(settingsPath);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(settingsFile, JsonStorage.Options);
                if (settings is not null)
                {
                    SetMaxConcurrentDownloads(settings.MaxConcurrentDownloads);
                    SetAgencyLimits(settings.MaxDownloadsPorOrgao, settings.LimitesPorOrgao);
                }
            }
            await LoadAgencyMapAsync();
            string own;
            lock (_queueLock) own = string.Join(", ", _agencyLimits.Select(kv => $"{kv.Key}={kv.Value}"));
            await Diagnostics.LogAsync(null, "info", "limits.loaded",
                $"Limites de download: {MaxConcurrentDownloads} simultâneos no total; {AgencyLimit("")} por órgão" +
                (own.Length > 0 ? $" (próprios: {own})" : "") + "; 1 por servidor de origem.");
            foreach (var receipt in await LoadSavedReceiptsAsync()) _receipts[receipt.Destino] = receipt;
            var stateChanged = false;
            foreach (var job in await LoadSavedJobsAsync())
            {
                var selectedForResume = resumeFilter is null || resumeFilter(job);
                var pending = job.Status is DownloadStatus.Baixando or DownloadStatus.NaFila or DownloadStatus.Pausado;
                var receipt = ReceiptFor(job.Destino);
                var alreadyRecorded = receipt is not null && receipt.VerificadoEmUtc >= job.CriadoEmUtc
                    && receipt.ArquivoId == job.ArquivoId && receipt.Url == job.Url
                    && receipt.Competencia == job.Competencia && receipt.Substituivel == job.Substituivel
                    && receipt.TamanhoEsperadoBytes == job.TamanhoEsperadoBytes
                    && string.Equals(receipt.Sha256Esperado, job.Sha256Esperado, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(job.Destino) && new FileInfo(job.Destino).Length == receipt.TamanhoLocalBytes
                    && File.GetLastWriteTimeUtc(job.Destino) == receipt.ModificadoEmUtc.UtcDateTime;
                var completedWithSameReceipt = receipt is not null && job.Status == DownloadStatus.Concluido
                    && job.VerificadoEmUtc == receipt.VerificadoEmUtc
                    && (job.BytesBaixados != receipt.TamanhoLocalBytes || job.TotalBytes != receipt.TamanhoLocalBytes
                        || job.FinalizadoEmUtc is null || job.Integridade is null);
                var restored = alreadyRecorded && (pending || completedWithSameReceipt)
                    ? job with
                    {
                        Status = DownloadStatus.Concluido,
                        BytesBaixados = receipt!.TamanhoLocalBytes,
                        TotalBytes = receipt.TamanhoLocalBytes,
                        UltimoProgressoEmUtc = receipt.VerificadoEmUtc,
                        FinalizadoEmUtc = job.FinalizadoEmUtc ?? receipt.VerificadoEmUtc,
                        VerificadoEmUtc = receipt.VerificadoEmUtc,
                        Integridade = job.Integridade ?? "Arquivo local conferido pelo comprovante",
                        Erro = null, CategoriaErro = null, TipoErro = null,
                        ForcarReinicio = false, PreservarAnterior = false
                    }
                    : pending && !selectedForResume
                    ? job
                    : pending && !resumePending
                    ? job with
                    {
                        Status = DownloadStatus.Pausado,
                        PausaAutomacao = job.Automatico || job.PausaAutomacao,
                        PausaManual = !job.Automatico || job.PausaManual
                    }
                    : pending && job.Automatico && !resumeAutomatic
                    ? job with { Status = DownloadStatus.Pausado, PausaAutomacao = true }
                    : pending && !job.PausaManual && !job.PausaAutomacao
                        ? job with { Status = DownloadStatus.NaFila }
                        : job;
                stateChanged |= restored != job;
                _jobs[restored.Id] = restored;
                if (restored.Status == DownloadStatus.NaFila && job.Status != DownloadStatus.NaFila)
                    await Diagnostics.LogAsync(restored.Id, "info", "job.recovered", "Transferência retomada após reabrir o aplicativo.");
            }
            var restoreHistory = Path.GetFileName(_statePath).Equals("downloads.json",
                StringComparison.OrdinalIgnoreCase);
            var missingReceipts = _receipts.Values.Where(receipt => restoreHistory &&
                ReceiptMatchesLocalFile(receipt) && !_jobs.Values.Any(job =>
                    job.Status == DownloadStatus.Concluido &&
                    job.Destino.Equals(receipt.Destino, StringComparison.OrdinalIgnoreCase) &&
                    job.VerificadoEmUtc == receipt.VerificadoEmUtc)).ToArray();
            if (missingReceipts.Length > 0)
            {
                var catalogPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "catalogo.json");
                var files = File.Exists(catalogPath)
                    ? (await new CatalogStore(catalogPath).LoadAsync()).Orgaos
                        .SelectMany(org => org.Funcoes.SelectMany(func => func.Arquivos))
                        .ToDictionary(file => file.Id, StringComparer.Ordinal)
                    : new Dictionary<string, ArquivoCatalogo>(StringComparer.Ordinal);
                foreach (var receipt in missingReceipts)
                {
                    files.TryGetValue(receipt.ArquivoId, out var file);
                    var restored = new DownloadJob(Guid.NewGuid(), receipt.ArquivoId,
                        file?.Nome ?? receipt.ArquivoId, receipt.Url, receipt.Destino,
                        DownloadStatus.Concluido, receipt.TamanhoLocalBytes,
                        receipt.TamanhoLocalBytes, ETag: receipt.ETag)
                    {
                        CriadoEmUtc = receipt.VerificadoEmUtc,
                        FinalizadoEmUtc = receipt.VerificadoEmUtc,
                        UltimoProgressoEmUtc = receipt.VerificadoEmUtc,
                        VerificadoEmUtc = receipt.VerificadoEmUtc,
                        Integridade = "Histórico recuperado do comprovante e do arquivo local.",
                        Tentativas = 1,
                        Competencia = receipt.Competencia,
                        PaginaOrigem = file?.PaginaOrigem,
                        Substituivel = receipt.Substituivel,
                        TamanhoEsperadoBytes = receipt.TamanhoEsperadoBytes,
                        Sha256Esperado = receipt.Sha256Esperado,
                        LastModifiedUtc = receipt.LastModifiedUtc
                    };
                    _jobs[restored.Id] = restored;
                }
                stateChanged = true;
                await Diagnostics.LogAsync(null, "info", "history.restored",
                    $"{missingReceipts.Length} download(s) concluído(s) recuperado(s) dos comprovantes locais.");
            }
            if (stateChanged) await SaveAsync();
            foreach (var failed in _jobs.Values.Where(j => j.Status == DownloadStatus.Falhou
                         && j.CategoriaErro is ("rede/tls" or "rede/conexao") && j.UltimoErroEmUtc is not null)
                         .OrderBy(j => j.UltimoErroEmUtc))
                _hostFailures.GetOrAdd(new Uri(failed.Url).Host, _ => new HostFailures())
                    .Record(failed.UltimoErroEmUtc!.Value);
            foreach (var limited in _jobs.Values.Where(j => j.CategoriaErro == "servidor/limite"
                         && j.ProximaTentativaEmUtc > _clock.GetUtcNow()))
                _hostFailures.GetOrAdd(new Uri(limited.Url).Host, _ => new HostFailures())
                    .DeferUntil(limited.ProximaTentativaEmUtc!.Value);
            _initialized = true;
            _ = Task.Run(SchedulerAsync);
            _ = Task.Run(StorageMonitorAsync);
            foreach (var job in _jobs.Values.Where(j => j.Status == DownloadStatus.NaFila
                         && (resumeFilter is null || resumeFilter(j)))
                         .OrderBy(j => j.CriadoEmUtc))
                Queue(job.Id, job.Prioritario);
            Changed?.Invoke();
        }
        finally { _initGate.Release(); }
    }

    private async Task<List<DownloadJob>> LoadSavedJobsAsync()
    {
        if (!File.Exists(_statePath)) return [];
        try { return await ReadJobsAsync(_statePath); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            var backup = _statePath + ".bak";
            if (!File.Exists(backup)) throw;
            var jobs = await ReadJobsAsync(backup);
            var corruptPath = _statePath + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.Move(_statePath, corruptPath);
            File.Copy(backup, _statePath);
            await Diagnostics.LogAsync(null, "warn", "state.recovered",
                "Fila recuperada de cópia de segurança.", $"Arquivo inválido preservado em {corruptPath}. Causa: {ex.Message}");
            return jobs;
        }
    }

    private static async Task<List<DownloadJob>> ReadJobsAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<DownloadJob>>(stream, JsonStorage.Options) ?? [];
    }

    private async Task<List<DownloadReceipt>> LoadSavedReceiptsAsync()
    {
        if (!File.Exists(_receiptsPath)) return [];
        try
        {
            await using var stream = File.OpenRead(_receiptsPath);
            return await JsonSerializer.DeserializeAsync<List<DownloadReceipt>>(stream, JsonStorage.Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            var backup = _receiptsPath + ".bak";
            if (!File.Exists(backup)) throw;
            await using var stream = File.OpenRead(backup);
            var receipts = await JsonSerializer.DeserializeAsync<List<DownloadReceipt>>(stream, JsonStorage.Options) ?? [];
            await Diagnostics.LogAsync(null, "warn", "receipts.recovered", "Comprovantes de download recuperados da cópia de segurança.", ex.Message);
            return receipts;
        }
    }

    private static bool ReceiptMatchesLocalFile(DownloadReceipt receipt)
    {
        var file = new FileInfo(receipt.Destino);
        return file.Exists && file.Length == receipt.TamanhoLocalBytes &&
            file.LastWriteTimeUtc == receipt.ModificadoEmUtc.UtcDateTime;
    }

    public async Task<DownloadJob> EnqueueAsync(ArquivoCatalogo arquivo, string targetPath,
        bool automatico = false, string? ifNoneMatch = null, bool priority = false)
    {
        if (!_initialized) throw new InvalidOperationException("Inicialize o gerenciador primeiro.");
        // Arquivo novo no catálogo: recarrega o mapa arquivo → órgão usado pelo limite por órgão.
        if (!_agencyByFile.ContainsKey(arquivo.Id)) await LoadAgencyMapAsync();
        await EnsureStorageAvailableAsync(targetPath);
        _originPolicy?.Validate(new Uri(arquivo.Url));
        await _jobGate.WaitAsync();
        try
        {
            var existing = _jobs.Values.FirstOrDefault(j => j.Status is
                    DownloadStatus.NaFila or DownloadStatus.Baixando or DownloadStatus.Pausado or DownloadStatus.Falhou
                && string.Equals(j.Destino, targetPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.Status is DownloadStatus.NaFila or DownloadStatus.Baixando)
                {
                    if (priority && existing.Status == DownloadStatus.NaFila)
                    {
                        Update(existing.Id, j => j with { Prioritario = true, Automatico = false });
                        Queue(existing.Id, true);
                        await SaveAsync();
                        await Diagnostics.LogAsync(existing.Id, "info", "job.prioritized", "Operador colocou o arquivo como próximo da fila.");
                    }
                    return _jobs[existing.Id];
                }
                throw new InvalidOperationException("Já existe um download pausado ou com falha para este destino. Retome-o na lista.");
            }
            var job = new DownloadJob(Guid.NewGuid(), arquivo.Id, arquivo.Nome, arquivo.Url, targetPath, DownloadStatus.NaFila)
            {
                CriadoEmUtc = _clock.GetUtcNow(),
                TamanhoEsperadoBytes = arquivo.TamanhoEsperadoBytes,
                Sha256Esperado = arquivo.Sha256,
                Competencia = arquivo.Competencia,
                PaginaOrigem = arquivo.PaginaOrigem,
                Substituivel = arquivo.Substituivel,
                Automatico = automatico,
                IfNoneMatch = ifNoneMatch,
                PreservarAnterior = File.Exists(targetPath),
                Prioritario = priority,
                LastModifiedUtc = ReceiptFor(targetPath)?.LastModifiedUtc
            };
            _jobs[job.Id] = job;
            await SaveAsync();
            await Diagnostics.LogAsync(job.Id, "info", "job.enqueued", "Arquivo adicionado à fila.",
                $"Fonte: {new Uri(job.Url).Host}; destino: {job.Destino}; competência: {job.Competencia ?? "não informada"}; " +
                $"substituível: {job.Substituivel}; página de origem: {job.PaginaOrigem ?? "não informada"}");
            Queue(job.Id, priority);
            Changed?.Invoke();
            return job;
        }
        finally { _jobGate.Release(); }
    }

    public async Task<DownloadJob> PrioritizeAsync(ArquivoCatalogo arquivo, string targetPath)
    {
        var existing = _jobs.Values.FirstOrDefault(j =>
            j.Status is (DownloadStatus.Falhou or DownloadStatus.Pausado)
            && j.ArquivoId == arquivo.Id && j.Url == arquivo.Url
            && string.Equals(j.Destino, targetPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await ResumeAsync(existing.Id, priority: true);
            return _jobs[existing.Id];
        }
        return await EnqueueAsync(arquivo, targetPath, priority: true);
    }

    public async Task ResumeAsync(Guid id, bool priority = false)
    {
        await _jobGate.WaitAsync();
        try
        {
            var waitUntil = DateTimeOffset.UtcNow.AddSeconds(5);
            while (_running.ContainsKey(id))
            {
                if (DateTimeOffset.UtcNow >= waitUntil)
                    throw new InvalidOperationException("A tentativa anterior ainda está finalizando. Tente novamente em instantes.");
                await Task.Delay(20);
            }
            if (!_jobs.TryGetValue(id, out var job) || job.Status is not (DownloadStatus.Pausado or DownloadStatus.Falhou)) return;
            await EnsureStorageAvailableAsync(job.Destino);
            if (_jobs.Values.Any(other => other.Id != id
                && other.Status is DownloadStatus.NaFila or DownloadStatus.Baixando
                && string.Equals(other.Destino, job.Destino, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Já existe um download ativo para este arquivo.");
            if (job.ForcarReinicio)
            {
                if (File.Exists(job.Destino + ".part")) File.Delete(job.Destino + ".part");
                await Diagnostics.LogAsync(id, "warn", "download.restart", "Arquivo com integridade inválida apagado antes de reiniciar.");
            }
            Update(id, current => current with
            {
                Status = DownloadStatus.NaFila,
                FinalizadoEmUtc = null, ForcarReinicio = false, PausaManual = false, PausaAutomacao = false,
                PausaArmazenamento = false,
                Erro = job.CategoriaErro == "armazenamento" ? null : current.Erro,
                CategoriaErro = job.CategoriaErro == "armazenamento" ? null : current.CategoriaErro,
                Prioritario = priority, Automatico = priority ? false : current.Automatico,
                ProximaTentativaEmUtc = null,
                BytesBaixados = job.ForcarReinicio ? 0 : current.BytesBaixados,
                TotalBytes = job.ForcarReinicio ? null : current.TotalBytes,
                ETag = job.ForcarReinicio ? null : current.ETag
            });
            await SaveAsync();
            await Diagnostics.LogAsync(id, "info", "job.resumed", "Operador retomou a transferência.");
            Queue(id, priority);
        }
        finally { _jobGate.Release(); }
    }

    public async Task PauseAsync(Guid id, bool automationPause = false)
    {
        if (!_jobs.TryGetValue(id, out var job)) return;
        if (job.Status == DownloadStatus.NaFila
            && _jobs.TryUpdate(id, job with
            {
                Status = DownloadStatus.Pausado,
                PausaManual = !automationPause || job.PausaManual,
                PausaAutomacao = automationPause || job.PausaAutomacao
            }, job))
        {
            Changed?.Invoke();
            await SaveAsync();
            await Diagnostics.LogAsync(id, "info", "job.paused", "Transferência pausada na fila.");
            return;
        }
        if (_jobs.TryGetValue(id, out var current) && current.Status == DownloadStatus.Baixando
            && _running.TryGetValue(id, out var cancellation))
        {
            Update(id, j => j with
            {
                PausaManual = !automationPause || j.PausaManual,
                PausaAutomacao = automationPause || j.PausaAutomacao
            });
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* A transferência terminou entre a consulta e o cancelamento. */ }
        }
    }

    public async Task MarkObsoleteAsync(Guid id, string reason)
    {
        await _jobGate.WaitAsync();
        try
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Status is not (DownloadStatus.Pausado or DownloadStatus.Falhou)) return;
            Update(id, j => j with { Status = DownloadStatus.Obsoleto, Erro = reason });
            await SaveAsync();
            await Diagnostics.LogAsync(id, "warn", "job.obsolete", "Job retirado da gestão automática.", reason);
        }
        finally { _jobGate.Release(); }
    }

    public async Task<int> ClearCompletedAsync()
    {
        var completed = _jobs.Values.Where(j => j.Status == DownloadStatus.Concluido).Select(j => j.Id).ToArray();
        foreach (var id in completed) _jobs.TryRemove(id, out _);
        if (completed.Length > 0)
        {
            await SaveAsync();
            await Diagnostics.LogAsync(null, "info", "history.cleared",
                $"{completed.Length} registro(s) concluído(s) removido(s) do histórico. Arquivos locais preservados.");
            Changed?.Invoke();
        }
        return completed.Length;
    }

    public void Stop()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        foreach (var cancellation in _running.Values)
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        if (!_initialized) return;
        while (!_running.IsEmpty)
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        await SaveAsync();
    }

    public async Task<StorageProtectionState> RefreshStorageAsync(string destination)
    {
        var wasBlocked = StorageState.Blocked;
        var (state, becameBlocked) = CheckStorage(destination);
        if (becameBlocked)
        {
            try { await SaveAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* O bloqueio em memória permanece ativo mesmo sem espaço para salvar o estado. */ }
            try { await Diagnostics.LogAsync(null, "error", "storage.minimum_free_reached", state.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* O aviso na interface permanece disponível. */ }
        }
        else if (wasBlocked && !state.Blocked) await SaveAsync();
        return state;
    }

    private async Task EnsureStorageAvailableAsync(string destination)
    {
        var state = await RefreshStorageAsync(destination);
        if (state.Blocked) throw new StorageBlockedException(state.Message);
    }

    private (StorageProtectionState State, bool BecameBlocked) CheckStorage(string destination)
    {
        StorageProtectionState state;
        bool becameBlocked;
        bool changed;
        CancellationTokenSource[] running = [];
        lock (_storageLock)
        {
            var checkTarget = _blockedDestination ?? destination;
            try
            {
                state = StorageProtection.Evaluate(_storageProbe(checkTarget));
                if (!state.Blocked && !checkTarget.Equals(destination, StringComparison.OrdinalIgnoreCase))
                {
                    checkTarget = destination;
                    state = StorageProtection.Evaluate(_storageProbe(checkTarget));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                state = new StorageProtectionState(true, checkTarget, 0, 0,
                    $"Downloads pausados: não foi possível verificar o espaço livre do destino. {ex.Message}");
            }
            var previous = _storageState;
            becameBlocked = state.Blocked && !previous.Blocked;
            changed = state.Blocked != previous.Blocked;
            Volatile.Write(ref _storageState, state);
            if (state.Blocked)
            {
                _blockedDestination = checkTarget;
                lock (_queueLock)
                {
                    _urgentQueue.Clear();
                    _normalQueue.Clear();
                    _queued.Clear();
                }
                foreach (var job in _jobs.Values.Where(j => j.Status == DownloadStatus.NaFila))
                {
                    while (_jobs.TryGetValue(job.Id, out var current)
                        && current.Status == DownloadStatus.NaFila
                        && !_jobs.TryUpdate(job.Id, current with
                        {
                            Status = DownloadStatus.Pausado, PausaArmazenamento = true,
                            Erro = state.Message, CategoriaErro = "armazenamento"
                        }, current)) { }
                }
                running = _running.Values.ToArray();
            }
            else _blockedDestination = null;
        }
        foreach (var cancellation in running)
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        if (changed) StorageChanged?.Invoke(state);
        if (becameBlocked) Changed?.Invoke();
        return (state, becameBlocked);
    }

    private async Task StorageMonitorAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token);
                try
                {
                    string[] targets = _blockedDestination is { } blocked
                        ? [blocked]
                        : _jobs.Values.Where(j => j.Status is DownloadStatus.Baixando or DownloadStatus.NaFila)
                            .Select(j => j.Destino).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (var target in targets)
                    {
                        if ((await RefreshStorageAsync(target)).Blocked) break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try { await Diagnostics.LogAsync(null, "error", "storage.monitor_failed",
                        "Falha ao verificar espaço livre; a próxima conferência será repetida.", ex.ToString()); }
                    catch { /* A próxima conferência ainda precisa ocorrer. */ }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task SchedulerAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _queueSignal.WaitAsync(_shutdown.Token);
                while (!_shutdown.IsCancellationRequested)
                {
                    Guid id;
                    string host;
                    string agency;
                    lock (_queueLock)
                    {
                        if (_activeDownloads >= MaxConcurrentDownloads || _queued.Count == 0) break;
                        var node = FindAvailable(_urgentQueue) ?? FindAvailable(_normalQueue);
                        if (node is null) break;
                        id = node.Value;
                        host = new Uri(_jobs[id].Url).Host;
                        agency = AgencyOf(_jobs[id]);
                        node.List!.Remove(node);
                        _queued.Remove(id);
                        _activeHosts.Add(host);
                        _activeAgencies[agency] = _activeAgencies.GetValueOrDefault(agency) + 1;
                        _activeDownloads++;
                    }
                    _ = Task.Run(() => RunScheduledJobAsync(id, host, agency));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Primeiro job da lista que respeita as duas regras de cortesia com os servidores do governo:
    /// um download por servidor de origem e no máximo o limite do órgão dono do arquivo.
    /// </summary>
    private LinkedListNode<Guid>? FindAvailable(LinkedList<Guid> list)
    {
        for (var node = list.First; node is not null; node = node.Next)
        {
            if (!_jobs.TryGetValue(node.Value, out var job) || _activeHosts.Contains(new Uri(job.Url).Host)) continue;
            var agency = AgencyOf(job);
            var limit = _agencyLimits.TryGetValue(agency, out var own) ? own : _defaultAgencyLimit;
            if (_activeAgencies.GetValueOrDefault(agency) < limit) return node;
        }
        return null;
    }

    private async Task RunScheduledJobAsync(Guid id, string host, string agency)
    {
        try { await RunJobAsync(id); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (StorageState.Blocked)
            {
                if (_jobs.TryGetValue(id, out var current)
                    && current.Status is DownloadStatus.NaFila or DownloadStatus.Baixando)
                    Update(id, j => j with
                    {
                        Status = DownloadStatus.Pausado, PausaArmazenamento = true,
                        Erro = StorageState.Message, CategoriaErro = "armazenamento"
                    });
            }
            else
            {
                try { await Diagnostics.LogAsync(id, "error", "worker.error", "Falha interna no worker.", ex.ToString()); }
                catch { /* Preserva o estado do job se o log local estiver indisponível. */ }
                if (_jobs.ContainsKey(id)) Update(id, j => j with
                {
                    Status = DownloadStatus.Falhou, Erro = ex.Message, CategoriaErro = "interno",
                    TipoErro = ex.GetType().Name, UltimoErroEmUtc = _clock.GetUtcNow()
                });
            }
        }
        finally
        {
            lock (_queueLock)
            {
                _activeDownloads--;
                _activeHosts.Remove(host);
                if (_activeAgencies.GetValueOrDefault(agency) <= 1) _activeAgencies.Remove(agency);
                else _activeAgencies[agency]--;
            }
            SignalQueue();
        }
    }

    private void SignalQueue()
    {
        if (_queueSignal.CurrentCount == 0)
            try { _queueSignal.Release(); }
            catch (SemaphoreFullException) { /* Outro produtor já acordou o agendador. */ }
    }

    private void Queue(Guid id, bool priority)
    {
        lock (_queueLock)
        {
            if (_queued.TryGetValue(id, out var node))
            {
                if (!priority) return;
                node.List!.Remove(node);
                _queued[id] = _urgentQueue.AddFirst(id);
                return;
            }
            _queued[id] = priority ? _urgentQueue.AddFirst(id) : _normalQueue.AddLast(id);
            SignalQueue();
        }
    }

    private async Task RunJobAsync(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var queued) || queued.Status != DownloadStatus.NaFila) return;
        if ((await RefreshStorageAsync(queued.Destino)).Blocked) return;
        var host = new Uri(queued.Url).Host;
        if (_hostFailures.TryGetValue(host, out var hostState)
            && hostState.CooldownUntil is { } blockedUntil && blockedUntil > _clock.GetUtcNow())
        {
            _deferredHosts.GetOrAdd(host, _ => new ConcurrentDictionary<Guid, byte>())[id] = 0;
            if (queued.ProximaTentativaEmUtc != blockedUntil)
            {
                Update(id, j => j with { ProximaTentativaEmUtc = blockedUntil });
                await SaveAsync();
                await Diagnostics.LogAsync(id, "warn", "host.deferred",
                    $"Aguardando o servidor {host} até {blockedUntil:O} antes de iniciar.");
            }
            StartHostTimer(host, blockedUntil);
            return;
        }
        using var ownCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _running[id] = ownCancellation;
        var started = queued with
        {
            Status = DownloadStatus.Baixando, IniciadoEmUtc = _clock.GetUtcNow(),
            FinalizadoEmUtc = null, Tentativas = queued.Tentativas + 1,
            Prioritario = false, ProximaTentativaEmUtc = null, CodigoHttp = null
        };
        if (!_jobs.TryUpdate(id, started, queued))
        {
            _running.TryRemove(id, out _);
            if (_jobs.TryGetValue(id, out var changed) && changed.Status == DownloadStatus.NaFila)
                Queue(id, changed.Prioritario);
            return;
        }
        Changed?.Invoke();
        try
        {
            await Diagnostics.LogAsync(id, "info", "job.started", $"Tentativa {started.Tentativas} iniciada.");
            await SaveAsync();
            if (started.PreservarAnterior && started.Tentativas == 1 && File.Exists(started.Destino + ".part"))
            {
                File.Delete(started.Destino + ".part");
                await Diagnostics.LogAsync(id, "warn", "download.restart", "Parcial anterior descartado para iniciar uma nova cópia.");
            }
            var isFtp = Uri.TryCreate(started.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeFtp;
            if (started.Substituivel && File.Exists(started.Destino + ".part")
                && (isFtp || !HasStrongETag(started.ETag)))
            {
                File.Delete(started.Destino + ".part");
                Update(id, j => j with { BytesBaixados = 0, TotalBytes = null, ETag = null });
                await Diagnostics.LogAsync(id, "warn", "download.mutable_restart",
                    "Fonte substituível sem validador HTTP forte; arquivo parcial descartado antes da tentativa.");
            }
            var notModified = false;
            if (isFtp)
            {
                var pinnedAddress = _originPolicy is null ? null :
                    await _originPolicy.ResolvePublicAsync(uri!, ownCancellation.Token);
                await _ftp.DownloadAsync(started, (bytes, total) =>
                {
                    if (CheckStorage(started.Destino).State.Blocked) return;
                    ReportProgress(id, bytes, total);
                }, ownCancellation.Token,
                    message => Diagnostics.LogAsync(id, "warn", "ftp.restart", message), pinnedAddress?.ToString());
            }
            else
            {
                _originPolicy?.Validate(uri!);
                notModified = await DownloadHttpAsync(id, ownCancellation.Token);
            }

            await EnsureStorageAvailableAsync(started.Destino);
            var integrity = notModified ? "Origem não alterada (HTTP 304)" : await VerifyAsync(id, ownCancellation.Token);
            if (!notModified)
            {
                File.Move(started.Destino + ".part", started.Destino, true);
                Update(id, j => j with { PreservarAnterior = true });
            }
            var receipt = await RecordReceiptAsync(_jobs[id]);
            _hostFailures.TryRemove(host, out _);
            Update(id, j => j with
            {
                Status = DownloadStatus.Concluido, Erro = null, CategoriaErro = null,
                UltimaFalha = null, UltimaCategoriaFalha = null, ProximaTentativaEmUtc = null,
                BytesBaixados = receipt.TamanhoLocalBytes, TotalBytes = receipt.TamanhoLocalBytes,
                UltimoProgressoEmUtc = receipt.VerificadoEmUtc,
                Integridade = integrity, FinalizadoEmUtc = receipt.VerificadoEmUtc,
                VerificadoEmUtc = receipt.VerificadoEmUtc,
                ForcarReinicio = false, PreservarAnterior = false
            });
            await Diagnostics.LogAsync(id, "info", "job.completed", "Transferência concluída.", integrity);
        }
        catch (OperationCanceledException)
        {
            if (StorageState.Blocked) await PauseForStorageAsync(id);
            else
            {
                Update(id, j => j with { Status = DownloadStatus.Pausado });
                await Diagnostics.LogAsync(id, "info", "job.paused", "Transferência interrompida; arquivo parcial preservado.");
            }
        }
        catch (StorageBlockedException) { await PauseForStorageAsync(id); }
        catch (Exception) when (StorageState.Blocked) { await PauseForStorageAsync(id); }
        catch (Exception ex)
        {
            var (category, code) = Classify(ex);
            var now = _clock.GetUtcNow();
            DateTimeOffset? hostCooldown = null;
            if (ex is RateLimitedException limited)
            {
                hostCooldown = _hostFailures.GetOrAdd(host, _ => new HostFailures())
                    .DeferUntil(limited.RetryAt);
                await Diagnostics.LogAsync(id, "warn", "host.rate_limited",
                    $"Servidor {host} pediu pausa até {hostCooldown:O}.");
            }
            else if (category is "rede/tls" or "rede/conexao")
            {
                var state = _hostFailures.GetOrAdd(host, _ => new HostFailures());
                hostCooldown = state.Record(now);
                if (hostCooldown is not null)
                    await Diagnostics.LogAsync(id, "warn", "host.cooldown",
                        $"Servidor {host} suspenso até {hostCooldown:O} após falhas consecutivas.");
            }
            var httpError = ex is HttpRequestException requestError ? requestError.HttpRequestError.ToString() : "—";
            await Diagnostics.LogAsync(id, "error", code, $"Transferência falhou: {ex.Message}",
                $"Servidor: {host}; erro HTTP: {httpError}; causa: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}\n{ex}");
            Update(id, j => j with
            {
                Status = DownloadStatus.Falhou, Erro = ex.Message, CategoriaErro = category,
                UltimaFalha = ex.Message, UltimaCategoriaFalha = category,
                TipoErro = ex.GetType().Name, UltimoErroEmUtc = now,
                FinalizadoEmUtc = now, ForcarReinicio = ex is IntegrityException,
                CodigoHttp = ex is HttpRequestException http && http.StatusCode is not null
                    ? (int)http.StatusCode.Value : j.CodigoHttp
            });
            Update(id, j => j with { ProximaTentativaEmUtc =
                hostCooldown is { } cooldown && cooldown > DownloadAutomation.RetryAt(j)
                    ? cooldown : DownloadAutomation.RetryAt(j) });
        }
        finally
        {
            _running.TryRemove(id, out _);
            try { await SaveAsync(); }
            catch (Exception ex) when (StorageState.Blocked && ex is IOException or UnauthorizedAccessException)
            { /* A retomada continuará protegida pela verificação do volume. */ }
        }
    }

    private async Task PauseForStorageAsync(Guid id)
    {
        var message = StorageState.Message;
        Update(id, job => job with
        {
            Status = DownloadStatus.Pausado, PausaArmazenamento = true,
            Erro = message, CategoriaErro = "armazenamento",
            UltimaFalha = message, UltimaCategoriaFalha = "armazenamento",
            UltimoErroEmUtc = _clock.GetUtcNow()
        });
        try
        {
            await Diagnostics.LogAsync(id, "error", "storage.download_paused",
                "Transferência interrompida pelo limite mínimo de espaço livre. O arquivo parcial foi preservado.", message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* O estado pausado em memória e o aviso na interface permanecem ativos. */ }
    }

    private static (string Category, string Code) Classify(Exception ex) => ex switch
    {
        IntegrityException => ("integridade", "integrity.failed"),
        RateLimitedException => ("servidor/limite", "http.rate_limited"),
        HttpRequestException h when h.StatusCode is HttpStatusCode.NotFound => ("fonte", "http.not_found"),
        HttpRequestException h when h.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ("acesso", "http.denied"),
        HttpRequestException h when IsTlsHandshakeFailure(h) => ("rede/tls", "tls.handshake_failed"),
        HttpRequestException h when InnerIs<SocketException>(h) => ("rede/conexao", "connection.failed"),
        HttpRequestException => ("rede/http", "http.failed"),
        TimeoutException => ("rede/tempo", "transfer.timeout"),
        FtpCommandException => ("servidor/ftp", "ftp.command_failed"),
        UnauthorizedAccessException => ("permissão local", "file.denied"),
        IOException => ("arquivo/rede", "io.failed"),
        InvalidDataException => ("resposta inválida", "data.invalid"),
        _ => ("interno", "job.failed")
    };

    private static bool InnerIs<T>(Exception exception) where T : Exception
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is T) return true;
        return false;
    }

    private static bool IsTlsHandshakeFailure(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.SecureConnectionError
        || InnerIs<AuthenticationException>(ex)
        || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase);

    private static bool IsRetryableTransportFailure(HttpRequestException ex) =>
        IsTlsHandshakeFailure(ex) || InnerIs<SocketException>(ex) || ex.HttpRequestError is
            HttpRequestError.ConnectionError or HttpRequestError.HttpProtocolError or HttpRequestError.ResponseEnded;

    private async Task<(HttpRequestMessage Request, HttpResponseMessage Response)> SendWithTlsRetryAsync(
        Guid id, Func<int, HttpRequestMessage> createRequest,
        CancellationTokenSource inactivity, CancellationToken cancellationToken)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            var request = createRequest(retry);
            try
            {
                var (sent, response) = _originPolicy is null
                    ? (request, await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, inactivity.Token))
                    : await SendWithRedirectGuardAsync(request, inactivity.Token);
                if (retry > 0)
                    await Diagnostics.LogAsync(id, "info", "tls.recovered",
                        $"Conexão recuperada na tentativa de transporte {retry + 1}; HTTP {response.Version}.");
                return (sent, response);
            }
            catch (HttpRequestException ex) when (retry < 2 && IsRetryableTransportFailure(ex))
            {
                request.Dispose();
                await Diagnostics.LogAsync(id, "warn", "connection.retry",
                    $"Conexão interrompida; nova tentativa em HTTP/1.1 ({retry + 2}/3).",
                    $"Causa: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
                await Task.Delay(TimeSpan.FromSeconds(retry == 0 ? 1 : 3), cancellationToken);
                inactivity.CancelAfter(TimeSpan.FromMinutes(3));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                request.Dispose();
                throw new TimeoutException("Servidor não respondeu por três minutos.");
            }
            catch
            {
                request.Dispose();
                throw;
            }
        }
        throw new InvalidOperationException("Falha inesperada na repetição do handshake TLS.");
    }

    private async Task<(HttpRequestMessage Request, HttpResponseMessage Response)> SendWithRedirectGuardAsync(
        HttpRequestMessage initial, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            _originPolicy!.Validate(current.RequestUri!);
            var response = await _http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther))
                return (current, response);
            if (redirects == 5 || response.Headers.Location is null)
            {
                response.Dispose();
                current.Dispose();
                throw new InvalidDataException("Cadeia de redirecionamento inválida.");
            }
            var nextUri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location
                : new Uri(current.RequestUri!, response.Headers.Location);
            _originPolicy.Validate(nextUri);
            var next = new HttpRequestMessage(HttpMethod.Get, nextUri);
            foreach (var header in current.Headers)
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Dispose();
            current.Dispose();
            current = next;
        }
        throw new InvalidOperationException("Redirecionamento fora do limite.");
    }

    private void StartHostTimer(string host, DateTimeOffset due)
    {
        if (!_hostTimers.TryAdd(host, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var delay = due - _clock.GetUtcNow();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _shutdown.Token);
                if (_deferredHosts.TryRemove(host, out var jobs))
                    foreach (var id in jobs.Keys)
                        if (_jobs.TryGetValue(id, out var job) && job.Status == DownloadStatus.NaFila)
                            Queue(id, job.Prioritario);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _hostTimers.TryRemove(host, out _);
                if (!_shutdown.IsCancellationRequested
                    && _deferredHosts.TryGetValue(host, out var pending) && !pending.IsEmpty)
                    StartHostTimer(host, _hostFailures.TryGetValue(host, out var state)
                        ? state.CooldownUntil ?? _clock.GetUtcNow() : _clock.GetUtcNow());
            }
        });
    }

    private sealed class HostFailures
    {
        private readonly Queue<DateTimeOffset> _recent = new();
        private DateTimeOffset? _cooldownUntil;
        public DateTimeOffset? CooldownUntil { get { lock (_recent) return _cooldownUntil; } }
        public DateTimeOffset? Record(DateTimeOffset now)
        {
            lock (_recent)
            {
                while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(10)) _recent.Dequeue();
                _recent.Enqueue(now);
                if (_recent.Count >= 3) _cooldownUntil = now.AddMinutes(15);
                return _cooldownUntil;
            }
        }
        public DateTimeOffset DeferUntil(DateTimeOffset due)
        {
            lock (_recent)
            {
                if (_cooldownUntil is null || due > _cooldownUntil) _cooldownUntil = due;
                return _cooldownUntil.Value;
            }
        }
    }

    private sealed class RateLimitedException(DateTimeOffset retryAt)
        : HttpRequestException("Servidor de origem limitou as requisições (HTTP 429).", null,
            HttpStatusCode.TooManyRequests)
    {
        public DateTimeOffset RetryAt { get; } = retryAt;
    }

    private static bool HasStrongETag(string? value) =>
        EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak;

    private async Task<string> VerifyAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = _jobs[id];
        var partial = job.Destino + ".part";
        var length = new FileInfo(partial).Length;
        if (job.TamanhoEsperadoBytes is long expected && length != expected)
            throw new IntegrityException($"Tamanho divergente: recebido {length} bytes; esperado {expected} bytes.");
        if (job.Sha256Esperado is not null)
        {
            await using var stream = File.OpenRead(partial);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            Update(id, j => j with { Sha256Calculado = actual });
            if (!actual.Equals(job.Sha256Esperado, StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException($"SHA-256 divergente: {actual}.");
            return "SHA-256 verificado";
        }
        return job.TamanhoEsperadoBytes is not null ? "Tamanho esperado verificado" : "Sem hash de referência";
    }

    private async Task<bool> DownloadHttpAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = _jobs[id];
        var target = job.Destino;
        var partial = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!job.PreservarAnterior && File.Exists(target) && !File.Exists(partial)) File.Move(target, partial);

        var conditionalTag = job.IfNoneMatch;
        var conditionalModified = job.LastModifiedUtc;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            HttpRequestMessage CreateRequest(int transportRetry)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, job.Url)
                {
                    Version = transportRetry == 0 ? HttpVersion.Version20 : HttpVersion.Version11,
                    VersionPolicy = transportRetry == 0 ? HttpVersionPolicy.RequestVersionOrLower
                        : HttpVersionPolicy.RequestVersionExact
                };
                if (existing == 0 && File.Exists(target))
                {
                    if (conditionalTag is { } previousTag
                        && EntityTagHeaderValue.TryParse(previousTag, out var parsedTag))
                        request.Headers.IfNoneMatch.Add(parsedTag);
                    else if (conditionalModified is { } modified)
                        request.Headers.IfModifiedSince = modified;
                }
                if (existing > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existing, null);
                    if (job.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak)
                        request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                }
                return request;
            }
            using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inactivity.CancelAfter(TimeSpan.FromMinutes(3));
            var (request, response) = await SendWithTlsRetryAsync(id, CreateRequest, inactivity, cancellationToken);
            using (request)
            using (response)
            {
            Update(id, j => j with { CodigoHttp = (int)response.StatusCode });
            await Diagnostics.LogAsync(id, "info", "http.response", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                $"Protocolo: HTTP/{response.Version}; Range solicitado desde: {existing}; Content-Range: {response.Content.Headers.ContentRange}; " +
                $"Content-Length: {response.Content.Headers.ContentLength}; ETag: {response.Headers.ETag}");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter;
                var due = retryAfter?.Date ?? _clock.GetUtcNow().Add(retryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                due = due < _clock.GetUtcNow().AddSeconds(30) ? _clock.GetUtcNow().AddSeconds(30) : due;
                due = due > _clock.GetUtcNow().AddDays(1) ? _clock.GetUtcNow().AddDays(1) : due;
                throw new RateLimitedException(due);
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var prior = ReceiptFor(target);
                var current = new FileInfo(target);
                if (request.Headers.IfNoneMatch.Count == 0 && request.Headers.IfModifiedSince is null
                    || prior is null || !current.Exists
                    || prior.ArquivoId != job.ArquivoId || prior.Url != job.Url
                    || prior.Competencia != job.Competencia || prior.Substituivel != job.Substituivel
                    || prior.TamanhoEsperadoBytes != job.TamanhoEsperadoBytes
                    || !string.Equals(prior.Sha256Esperado, job.Sha256Esperado, StringComparison.OrdinalIgnoreCase)
                    || current.Length != prior.TamanhoLocalBytes
                    || current.LastWriteTimeUtc != prior.ModificadoEmUtc.UtcDateTime)
                {
                    conditionalTag = null;
                    conditionalModified = null;
                    Update(id, j => j with { LastModifiedUtc = null });
                    await Diagnostics.LogAsync(id, "warn", "http.conditional_restart",
                        "Resposta 304 sem cópia local comprovada; repetindo sem condição.");
                    continue;
                }
                if (File.Exists(partial)) File.Delete(partial);
                Update(id, j => j with
                {
                    BytesBaixados = current.Length, TotalBytes = current.Length,
                    ETag = response.Headers.ETag?.ToString() ?? j.IfNoneMatch,
                    LastModifiedUtc = response.Content.Headers.LastModified ?? j.LastModifiedUtc
                });
                return true;
            }

            if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (!job.Substituivel && response.Content.Headers.ContentRange?.Length == existing)
                {
                    ReportProgress(id, existing, existing);
                    return false;
                }
                File.Delete(partial);
                await Diagnostics.LogAsync(id, "warn", "download.restart", "Servidor rejeitou o intervalo; arquivo parcial apagado.");
                continue;
            }

            response.EnsureSuccessStatusCode();
            var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange?.From == existing
                && (!job.Substituivel || (HasStrongETag(job.ETag)
                    && string.Equals(response.Headers.ETag?.ToString(), job.ETag, StringComparison.Ordinal)));
            if (response.StatusCode == HttpStatusCode.PartialContent && !append)
            {
                File.Delete(partial);
                await Diagnostics.LogAsync(id, "warn", "download.restart", "Content-Range não corresponde ao arquivo parcial.");
                continue;
            }
            if (existing > 0 && !append)
            {
                File.Delete(partial);
                await Diagnostics.LogAsync(id, "warn", "download.restart", "Servidor ignorou Range; download reiniciado do zero.");
            }
            var offset = append ? existing : 0;
            var total = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength is long length ? length + offset : null);
            var etag = response.Headers.ETag?.ToString();
            Update(id, j => j with { BytesBaixados = offset, TotalBytes = total,
                ETag = etag ?? (append ? j.ETag : null),
                LastModifiedUtc = response.Content.Headers.LastModified ?? (append ? j.LastModifiedUtc : null) });

            await EnsureStorageAvailableAsync(target);
            var written = offset;
            await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read, 256 * 1024, FileOptions.Asynchronous))
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[256 * 1024];
                var lastReport = Environment.TickCount64;
                var lastStorageCheck = lastReport;
                var lastCheckedBytes = written;
                int read;
                while (true)
                {
                    inactivity.CancelAfter(TimeSpan.FromMinutes(3));
                    try { read = await input.ReadAsync(buffer, inactivity.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new TimeoutException("Transferência sem dados por três minutos."); }
                    if (read == 0) break;
                    if (written - lastCheckedBytes >= 8 * 1024 * 1024
                        || Environment.TickCount64 - lastStorageCheck >= 1000)
                    {
                        await EnsureStorageAvailableAsync(target);
                        lastCheckedBytes = written;
                        lastStorageCheck = Environment.TickCount64;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    if (Environment.TickCount64 - lastReport >= 250)
                    {
                        ReportProgress(id, written, total);
                        lastReport = Environment.TickCount64;
                    }
                }
                await output.FlushAsync(cancellationToken);
            }
            await EnsureStorageAvailableAsync(target);
            if (total is long expected && written != expected)
                throw new IOException($"Download incompleto: {written} de {expected} bytes.");
            ReportProgress(id, written, total ?? written);
            return false;
            }
        }
        throw new InvalidDataException("O servidor não permitiu continuar o arquivo; tente novamente.");
    }

    private async Task<DownloadReceipt> RecordReceiptAsync(DownloadJob job)
    {
        var local = new FileInfo(job.Destino);
        if (!local.Exists) throw new FileNotFoundException("Arquivo concluído não encontrado para registrar o comprovante.", job.Destino);
        var receipt = new DownloadReceipt(job.ArquivoId, job.Url, job.Destino, job.Competencia,
            job.Substituivel, job.TamanhoEsperadoBytes, job.Sha256Esperado, _clock.GetUtcNow(),
            local.Length, new DateTimeOffset(local.LastWriteTimeUtc, TimeSpan.Zero), job.ETag)
        { LastModifiedUtc = job.LastModifiedUtc };
        await _receiptGate.WaitAsync();
        try
        {
            var next = _receipts.Values.Where(r => !r.Destino.Equals(job.Destino, StringComparison.OrdinalIgnoreCase))
                .Append(receipt).OrderBy(r => r.Destino).ToArray();
            await JsonStorage.SaveAsync(_receiptsPath, next);
            _receipts[job.Destino] = receipt;
            return receipt;
        }
        finally { _receiptGate.Release(); }
    }

    private void ReportProgress(Guid id, long bytes, long? total) => Update(id, j => j with
    {
        BytesBaixados = bytes, TotalBytes = total, UltimoProgressoEmUtc = _clock.GetUtcNow()
    });

    private void Update(Guid id, Func<DownloadJob, DownloadJob> change)
    {
        _jobs.AddOrUpdate(id, _ => throw new InvalidOperationException(), (_, current) => change(current));
        Changed?.Invoke();
    }

    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try { await JsonStorage.SaveAsync(_statePath, Jobs); }
        finally { _saveGate.Release(); }
    }

    public void Dispose() => Stop();

    private sealed class IntegrityException(string message) : Exception(message);
    private sealed class StorageBlockedException(string message) : InvalidOperationException(message);
}

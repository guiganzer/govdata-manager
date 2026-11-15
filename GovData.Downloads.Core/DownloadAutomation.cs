using System.Net.Http.Headers;
using System.Text.Json;

namespace GovData.Downloads.Core;

public sealed record AutomationSnapshot(
    bool Enabled, DateTimeOffset? LastScanUtc, DateTimeOffset? NextScanUtc,
    int Sources, int UpToDate, int Pending, int Attention,
    DateTimeOffset? NextRetryUtc, string? LastError);

public sealed class DownloadAutomation : IDisposable
{
    public static readonly TimeSpan MutableRefreshInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    private readonly CatalogStore _catalog;
    private readonly DownloadManager _downloads;
    private readonly TimeProvider _clock;
    private readonly AppSettingsStore _settings;
    private readonly string _automationPath;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _started;
    private bool _enabled = true;
    private bool _profileEnabled = true;
    private string? _settingsError;

    public event Action? Changed;
    public bool ProfileEnabled => _profileEnabled;
    public AutomationSnapshot Snapshot { get; private set; } = new(true, null, null, 0, 0, 0, 0, null, null);

    public DownloadAutomation(CatalogStore catalog, DownloadManager downloads)
        : this(catalog, downloads, TimeProvider.System,
            new AppSettingsStore(Path.Combine(Path.GetDirectoryName(catalog.Path)!, "settings.json"))) { }

    public DownloadAutomation(CatalogStore catalog, DownloadManager downloads, TimeProvider clock)
        : this(catalog, downloads, clock,
            new AppSettingsStore(Path.Combine(Path.GetDirectoryName(catalog.Path)!, "settings.json"))) { }

    public DownloadAutomation(CatalogStore catalog, DownloadManager downloads, AppSettingsStore settings)
        : this(catalog, downloads, TimeProvider.System, settings) { }

    public DownloadAutomation(CatalogStore catalog, DownloadManager downloads, TimeProvider clock, AppSettingsStore settings)
    {
        _catalog = catalog;
        _downloads = downloads;
        _clock = clock;
        _settings = settings;
        var folder = Path.GetDirectoryName(catalog.Path)!;
        _automationPath = Path.Combine(folder, "automation.json");
        _catalog.Saved += RequestCheck;
        _downloads.StorageChanged += OnStorageChanged;
    }

    private void OnStorageChanged(StorageProtectionState _) => RequestCheck();

    public async Task StartAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            if (_started) return;
            _profileEnabled = (await _settings.LoadAsync()).Profile?.AutomationEnabled ?? true;
            if (File.Exists(_automationPath))
            {
                try { _enabled = await ReadEnabledAsync(_automationPath); }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    var backup = _automationPath + ".bak";
                    if (File.Exists(backup))
                    {
                        try
                        {
                            _enabled = await ReadEnabledAsync(backup);
                            await _downloads.Diagnostics.LogAsync(null, "warn", "automation.settings_recovered",
                                "Configuração da automação recuperada da cópia de segurança.", ex.Message);
                        }
                        catch (Exception backupError) when (backupError is JsonException or IOException)
                        {
                            _enabled = false;
                            _settingsError = "Configuração da automação inválida; automação pausada.";
                        }
                    }
                    else
                    {
                        _enabled = false;
                        _settingsError = "Configuração da automação inválida; automação pausada.";
                    }
                    if (_settingsError is not null)
                        await _downloads.Diagnostics.LogAsync(null, "error", "automation.settings_invalid",
                            _settingsError, ex.ToString());
                }
            }
            Snapshot = Snapshot with { Enabled = _enabled, LastError = _settingsError };
            if (!_profileEnabled)
                await _downloads.InitializeAsync(resumeAutomatic: false, resumePending: false);
            _started = true;
            _loop = Task.Run(RunLoopAsync);
            Changed?.Invoke();
        }
        finally { _startGate.Release(); }
        if (_profileEnabled) await CheckNowAsync();
    }

    public async Task CheckNowAsync()
    {
        if (!_profileEnabled) return;
        try { await ReconcileAsync(_stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Snapshot = Snapshot with { LastError = ex.Message, LastScanUtc = _clock.GetUtcNow() };
            Changed?.Invoke();
            await _downloads.Diagnostics.LogAsync(null, "error", "automation.scan_failed",
                "Falha na reconciliação automática do catálogo.", ex.ToString());
        }
    }

    public void RequestCheck()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _scanGate.WaitAsync();
        try
        {
            if (!_profileEnabled) throw new InvalidOperationException("O perfil manual desabilita a automação.");
            await JsonStorage.SaveAsync(_automationPath, new AutomationSettings(enabled));
            _enabled = enabled;
            _settingsError = null;
            Snapshot = Snapshot with { Enabled = enabled, LastError = null };
            Changed?.Invoke();
            if (!enabled)
            {
                foreach (var job in _downloads.Jobs.Where(j => j.Automatico
                    && j.Status is DownloadStatus.NaFila or DownloadStatus.Baixando))
                    await _downloads.PauseAsync(job.Id, automationPause: true);
            }
        }
        finally { _scanGate.Release(); }
        if (enabled)
        {
            RequestCheck();
            await CheckNowAsync();
        }
    }

    public async Task SetProfileEnabledAsync(bool enabled)
    {
        await StartAsync();
        await _scanGate.WaitAsync();
        try
        {
            if (_profileEnabled == enabled) return;
            await _settings.UpdateAsync(settings => settings.Profile.AutomationEnabled = enabled);
            _profileEnabled = enabled;
            if (!enabled)
            {
                foreach (var job in _downloads.Jobs.Where(j => j.Automatico
                    && j.Status is DownloadStatus.NaFila or DownloadStatus.Baixando))
                    await _downloads.PauseAsync(job.Id, automationPause: true);
            }
            Changed?.Invoke();
        }
        finally { _scanGate.Release(); }
        if (enabled) await CheckNowAsync();
    }

    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        ReconcileAsync(orgaoId: null, cancellationToken: cancellationToken);

    /// <summary>Incrementally reconciles one agency without starting the full-catalog automation loop.</summary>
    public async Task ReconcileAgencyAsync(string orgaoId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orgaoId)) throw new ArgumentException("Informe o ID do órgão.", nameof(orgaoId));

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await _settings.LoadAsync(cancellationToken);
            _profileEnabled = settings.Profile?.AutomationEnabled ?? true;
            _enabled = File.Exists(_automationPath) ? await ReadEnabledAsync(_automationPath) : true;
            if (!_profileEnabled)
                throw new InvalidOperationException("O perfil manual desabilita a automação de downloads.");
            if (!_enabled)
                throw new InvalidOperationException("A automação de downloads está desabilitada em automation.json.");
        }
        finally { _startGate.Release(); }

        await ReconcileAsync(orgaoId, cancellationToken);
    }

    private async Task ReconcileAsync(string? orgaoId, CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            if (!_profileEnabled) return;
            await _catalog.EnsureCreatedAsync(cancellationToken);
            var catalog = await _catalog.LoadAsync(cancellationToken);
            var root = await DownloadRootAsync(cancellationToken);
            var agencies = catalog.Orgaos
                .Where(o => orgaoId is null || string.Equals(o.Id, orgaoId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (orgaoId is not null && agencies.Length == 0)
                throw new KeyNotFoundException($"Órgão '{orgaoId}' não existe no catálogo v2.");
            var agencyRoots = agencies.Select(o => DownloadPaths.ForAgency(root, o)).ToArray();
            var sources = agencies.SelectMany(o => o.Funcoes.SelectMany(f =>
                f.Arquivos.Select(a => new Source(a, DownloadPaths.ForFile(root, o, f, a)))))
                .ToDictionary(s => s.Target, StringComparer.OrdinalIgnoreCase);
            // A execução por órgão preserva o estado dos demais, mas só retoma jobs
            // cujos destinos pertencem ao escopo desta chamada.
            await _downloads.InitializeAsync(resumeAutomatic: _enabled, resumePending: true,
                resumeFilter: orgaoId is null ? null : job => agencyRoots.Any(path => DownloadPaths.IsWithinAgency(job.Destino, path)));
            var storage = await _downloads.RefreshStorageAsync(root);
            if (storage.Blocked)
            {
                Snapshot = Snapshot with { LastError = storage.Message, LastScanUtc = _clock.GetUtcNow() };
                Changed?.Invoke();
                return;
            }
            var tombstones = (await new DownloadTombstoneStore(Path.Combine(
                Path.GetDirectoryName(_catalog.Path)!, "download-tombstones.json")).LoadAsync(cancellationToken))
                .ToDictionary(x => x.Destino, StringComparer.OrdinalIgnoreCase);
            var now = _clock.GetUtcNow();
            var upToDate = 0;
            var pending = 0;
            var attention = 0;
            DateTimeOffset? nextRetry = null;

            if (_enabled)
            {
                foreach (var old in _downloads.Jobs.Where(j => j.Automatico && j.Status is
                    (DownloadStatus.NaFila or DownloadStatus.Baixando or DownloadStatus.Pausado or DownloadStatus.Falhou)
                    && (orgaoId is null || agencyRoots.Any(path => DownloadPaths.IsWithinAgency(j.Destino, path)))))
                {
                    if (sources.TryGetValue(old.Destino, out var source) && Matches(old, source)) continue;
                    if (old.Status is DownloadStatus.NaFila or DownloadStatus.Baixando)
                        await _downloads.PauseAsync(old.Id, automationPause: true);
                    else
                        await _downloads.MarkObsoleteAsync(old.Id, "Fonte removida ou alterada no catálogo.");
                }

                foreach (var source in sources.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var receipt = _downloads.ReceiptFor(source.Target);
                    var current = _downloads.Jobs.FirstOrDefault(j => j.Destino.Equals(source.Target, StringComparison.OrdinalIgnoreCase)
                        && j.Status is (DownloadStatus.NaFila or DownloadStatus.Baixando or DownloadStatus.Pausado or DownloadStatus.Falhou));
                    if (current is not null)
                    {
                        if (!Matches(current, source)) { pending++; continue; }
                        if (!current.Automatico)
                        {
                            if (current.Status is DownloadStatus.NaFila or DownloadStatus.Baixando) pending++;
                            else attention++;
                            continue;
                        }
                        if (current.Status is DownloadStatus.NaFila or DownloadStatus.Baixando)
                        {
                            pending++;
                            if (current.ProximaTentativaEmUtc is { } delayed)
                                nextRetry = Min(nextRetry, delayed);
                            continue;
                        }
                        if (current.Status == DownloadStatus.Pausado)
                        {
                            if (current.PausaManual) { attention++; continue; }
                            await _downloads.ResumeAsync(current.Id);
                            pending++;
                            continue;
                        }
                    if (current.Status == DownloadStatus.Falhou)
                        {
                        if (current.PausaManual) { attention++; continue; }
                            var due = RetryAt(current);
                            if (now >= due) { await _downloads.ResumeAsync(current.Id); pending++; }
                            else { attention++; nextRetry = Min(nextRetry, due); }
                            continue;
                        }
                    }

                    if (ReceiptMatches(receipt, source))
                    {
                        if (!source.File.Substituivel || now < NextVerificationAt(source.File, receipt!.VerificadoEmUtc))
                        { upToDate++; continue; }
                    }
                    if (!File.Exists(source.Target) && receipt is not null &&
                        tombstones.TryGetValue(source.Target, out var tombstone) &&
                        tombstone.ArquivoId == source.File.Id && tombstone.Url == source.File.Url &&
                        tombstone.Competencia == source.File.Competencia)
                    {
                        if (!source.File.Substituivel || now < NextVerificationAt(source.File, tombstone.CheckedAt))
                        { upToDate++; continue; }
                    }

                    var tag = receipt is not null && ReceiptMatches(receipt, source)
                        && EntityTagHeaderValue.TryParse(receipt.ETag, out _) ? receipt.ETag : null;
                    await _downloads.EnqueueAsync(source.File, source.Target, automatico: true, ifNoneMatch: tag);
                    pending++;
                }
            }
            Snapshot = new AutomationSnapshot(_enabled, now, now + ScanInterval, sources.Count,
                upToDate, pending, attention, nextRetry, _settingsError);
            Changed?.Invoke();
        }
        finally { _scanGate.Release(); }
    }

    private async Task<string> DownloadRootAsync(CancellationToken cancellationToken)
    {
        var defaultRoot = Path.Combine(Path.GetDirectoryName(_catalog.Path)!, "downloads");
        var settings = await _settings.LoadAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(settings?.DownloadRoot) ? defaultRoot : settings.DownloadRoot;
    }

    private static async Task<bool> ReadEnabledAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return (await JsonSerializer.DeserializeAsync<AutomationSettings>(stream, JsonStorage.Options))?.Enabled
            ?? throw new JsonException("Configuração vazia.");
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _wake.WaitAsync(ScanInterval, _stop.Token);
                if (_profileEnabled) await CheckNowAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool Matches(DownloadJob job, Source source) =>
        job.ArquivoId == source.File.Id && job.Url == source.File.Url
        && job.Destino.Equals(source.Target, StringComparison.OrdinalIgnoreCase)
        && job.Competencia == source.File.Competencia
        && job.Substituivel == source.File.Substituivel
        && job.TamanhoEsperadoBytes == source.File.TamanhoEsperadoBytes
        && string.Equals(job.Sha256Esperado, source.File.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool ReceiptMatches(DownloadReceipt? receipt, Source source)
    {
        if (receipt is null || receipt.ArquivoId != source.File.Id || receipt.Url != source.File.Url
            || receipt.Competencia != source.File.Competencia || receipt.Substituivel != source.File.Substituivel
            || receipt.TamanhoEsperadoBytes != source.File.TamanhoEsperadoBytes
            || !string.Equals(receipt.Sha256Esperado, source.File.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        var local = new FileInfo(source.Target);
        return local.Exists && local.Length == receipt.TamanhoLocalBytes
            && local.LastWriteTimeUtc == receipt.ModificadoEmUtc.UtcDateTime;
    }

    public static DateTimeOffset RetryAt(DownloadJob job)
    {
        if (job.ProximaTentativaEmUtc is { } scheduled) return scheduled;
        var last = job.UltimoErroEmUtc ?? job.FinalizadoEmUtc ?? job.CriadoEmUtc;
        if (job.CodigoHttp is 401 or 403 or 404 || job.CategoriaErro is "integridade" or "resposta inválida")
            return last + TimeSpan.FromHours(24);
        var minutes = Math.Min(60, 1 << Math.Clamp(job.Tentativas - 1, 0, 6));
        return last + TimeSpan.FromMinutes(minutes);
    }

    public static DateTimeOffset NextVerificationAt(ArquivoCatalogo file, DateTimeOffset verifiedAt) =>
        file.FrequenciaVerificacao switch
        {
            "diaria" => verifiedAt + MutableRefreshInterval,
            "mensal" => verifiedAt.AddMonths(1),
            "anual" => verifiedAt.AddYears(1),
            null when file.Competencia is not null => verifiedAt.AddMonths(1),
            _ => verifiedAt + MutableRefreshInterval
        };

    private static DateTimeOffset? Min(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is null || candidate < current ? candidate : current;

    public void Stop()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _catalog.Saved -= RequestCheck;
        _downloads.StorageChanged -= OnStorageChanged;
    }

    private sealed record Source(ArquivoCatalogo File, string Target);
    private sealed record AutomationSettings(bool Enabled);
}

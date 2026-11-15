using System.Runtime.InteropServices;
using System.Text.Json;
using GovData.Downloads.Core;

internal static class AutonomousDownloads
{
    private static readonly HashSet<string> Actions =
        ["check-now", "pause-auto", "resume-auto", "pause-job", "resume-job"];

    private sealed class ControlRequest
    {
        public int SchemaVersion { get; set; }
        public string Action { get; set; } = "";
        public Guid? JobId { get; set; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = args[0];
            var options = ParseOptions(args.Skip(command == "control" ? 2 : 1).ToArray());
            var home = Path.GetFullPath(options.GetValueOrDefault("home")
                ?? Environment.GetEnvironmentVariable("GOVDATA_HOME")
                ?? throw new ArgumentException("Defina GOVDATA_HOME ou --home."));
            if (command == "run")
            {
                if (options.Count != 0 && options.Keys.Any(x => x != "home"))
                    throw new ArgumentException("run aceita somente --home.");
                return await RunServiceAsync(home);
            }
            if (command == "status")
            {
                var path = Path.Combine(home, "downloads-service.json");
                if (!File.Exists(path)) throw new FileNotFoundException("Serviço de downloads ainda não gerou status.", path);
                Console.WriteLine(await File.ReadAllTextAsync(path));
                return 0;
            }
            if (args.Length < 2 || !Actions.Contains(args[1]))
                throw new ArgumentException("Ação de controle inválida.");
            var action = args[1];
            Guid? jobId = null;
            if (options.TryGetValue("job-id", out var jobText))
            {
                if (!Guid.TryParse(jobText, out var parsed)) throw new ArgumentException("--job-id deve ser UUID.");
                jobId = parsed;
            }
            if (action.EndsWith("-job", StringComparison.Ordinal) != jobId.HasValue)
                throw new ArgumentException("--job-id é obrigatório somente para pause-job/resume-job.");
            if (options.Keys.Any(x => x is not ("home" or "job-id")))
                throw new ArgumentException("Opção desconhecida em control.");
            var inbox = Path.Combine(home, "downloads-control", "inbox");
            Directory.CreateDirectory(inbox);
            if (OperatingSystem.IsLinux())
            {
                var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(Path.GetDirectoryName(inbox)!, privateMode);
                File.SetUnixFileMode(inbox, privateMode);
            }
            var id = Guid.NewGuid().ToString("N");
            var target = Path.Combine(inbox, id + ".json");
            var temporary = Path.Combine(inbox, "." + id + ".tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ControlRequest
            { SchemaVersion = 1, Action = action, JobId = jobId }, JsonStorage.Options));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, target);
            Console.WriteLine($"Comando {id} enfileirado para o serviço local de downloads.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length ||
                args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Argumento inválido: {args[i]}");
            result[args[i][2..]] = args[++i];
        }
        return result;
    }

    private static async Task<int> RunServiceAsync(string home)
    {
        var catalogPath = Path.Combine(home, "catalogo.json");
        if (!File.Exists(catalogPath)) throw new FileNotFoundException("Catálogo v2 ausente.", catalogPath);
        var originsPath = Path.Combine(home, "server", "download-origins.json");
        if (!File.Exists(originsPath)) originsPath = Path.Combine(AppContext.BaseDirectory, "download-origins.json");
        if (!File.Exists(originsPath)) throw new FileNotFoundException("Política de origens ausente.", originsPath);
        using var owner = DownloadStateOwner.Acquire(home);
        var policy = DownloadOriginPolicy.Load(originsPath);
        using var http = policy.CreateHttpClient();
        using var downloads = new DownloadManager(http, Path.Combine(home, "downloads.json"),
            TimeProvider.System, policy);
        using var automation = new DownloadAutomation(new CatalogStore(catalogPath), downloads,
            new AppSettingsStore(Path.Combine(home, "settings.json")));
        using var stop = new CancellationTokenSource();
        using var signal = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); })
            : null;
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        var controlRoot = Path.Combine(home, "downloads-control");
        var inbox = Path.Combine(controlRoot, "inbox");
        var done = Path.Combine(controlRoot, "done");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(done);
        if (OperatingSystem.IsLinux())
        {
            var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(controlRoot, privateMode);
            File.SetUnixFileMode(inbox, privateMode);
            File.SetUnixFileMode(done, privateMode);
        }
        try
        {
            await automation.StartAsync();
            Console.WriteLine("Downloads autônomo ativo; catálogo v2 monitorado; sem API ou cadastro no Bronze.");
            while (!stop.IsCancellationRequested)
            {
                foreach (var path in Directory.EnumerateFiles(inbox, "*.json").Order(StringComparer.Ordinal))
                {
                    await ProcessControlAsync(path, done, automation, downloads);
                    if (stop.IsCancellationRequested) break;
                }
                await WriteStatusAsync(home, automation, downloads, active: true);
                try { await Task.Delay(TimeSpan.FromSeconds(2), stop.Token); }
                catch (OperationCanceledException) { break; }
            }
            return 0;
        }
        finally
        {
            automation.Stop();
            await downloads.StopAsync(CancellationToken.None);
            await WriteStatusAsync(home, automation, downloads, active: false);
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task ProcessControlAsync(string path, string done, DownloadAutomation automation,
        DownloadManager downloads)
    {
        var name = Path.GetFileName(path);
        var resultPath = Path.Combine(done, name);
        object result;
        try
        {
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Comando excede 4096 bytes.");
            var request = JsonSerializer.Deserialize<ControlRequest>(await File.ReadAllTextAsync(path), JsonStorage.Options)
                ?? throw new InvalidDataException("Comando vazio.");
            if (request.SchemaVersion != 1 || !Actions.Contains(request.Action))
                throw new InvalidDataException("Contrato ou ação inválidos.");
            switch (request.Action)
            {
                case "check-now": await automation.CheckNowAsync(); break;
                case "pause-auto": await automation.SetEnabledAsync(false); break;
                case "resume-auto":
                    if (!automation.ProfileEnabled) await automation.SetProfileEnabledAsync(true);
                    await automation.SetEnabledAsync(true);
                    break;
                case "pause-job" when request.JobId is { } pauseId:
                    if (!downloads.Jobs.Any(x => x.Id == pauseId)) throw new KeyNotFoundException("Job não encontrado.");
                    await downloads.PauseAsync(pauseId); break;
                case "resume-job" when request.JobId is { } resumeId:
                    if (!downloads.Jobs.Any(x => x.Id == resumeId)) throw new KeyNotFoundException("Job não encontrado.");
                    await downloads.ResumeAsync(resumeId, priority: true); break;
                default: throw new InvalidDataException("jobId obrigatório para ação de job.");
            }
            result = new { schemaVersion = 1, ok = true, request.Action, request.JobId,
                completedAt = DateTimeOffset.UtcNow };
            Console.WriteLine($"Controle aplicado: {request.Action} {request.JobId}");
        }
        catch (Exception ex)
        {
            result = new { schemaVersion = 1, ok = false, error = ex.Message,
                completedAt = DateTimeOffset.UtcNow };
            Console.Error.WriteLine($"Controle {name}: {ex.Message}");
        }
        await JsonStorage.SaveAsync(resultPath, result);
        File.Delete(path);
    }

    private static Task WriteStatusAsync(string home, DownloadAutomation automation,
        DownloadManager downloads, bool active)
    {
        var jobs = downloads.Jobs;
        var value = new
        {
            schemaVersion = 1, active, updatedAt = DateTimeOffset.UtcNow,
            automation = automation.Snapshot,
            profileEnabled = automation.ProfileEnabled,
            jobs = new
            {
                queued = jobs.Count(x => x.Status == DownloadStatus.NaFila),
                downloading = jobs.Count(x => x.Status == DownloadStatus.Baixando),
                paused = jobs.Count(x => x.Status == DownloadStatus.Pausado),
                failed = jobs.Count(x => x.Status == DownloadStatus.Falhou),
                completed = jobs.Count(x => x.Status == DownloadStatus.Concluido)
            }
        };
        return JsonStorage.SaveAsync(Path.Combine(home, "downloads-service.json"), value);
    }
}

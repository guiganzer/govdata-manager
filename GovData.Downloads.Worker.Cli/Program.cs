using System.Text.Json;
using GovData.Downloads.Core;

return await WorkerProgram.RunAsync(args);

internal static class WorkerProgram
{
    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] is ("run" or "control" or "status"))
            return await AutonomousDownloads.RunAsync(args);
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        if (!string.Equals(args[0], "run-agency", StringComparison.Ordinal))
        {
            EmitError($"Comando desconhecido: {args[0]}");
            PrintUsage();
            return 2;
        }

        Dictionary<string, string> options;
        try { options = ParseOptions(args.Skip(1).ToArray()); }
        catch (ArgumentException ex)
        {
            EmitError(ex.Message);
            return 2;
        }
        if (!options.TryGetValue("organization-id", out var agencyId) || string.IsNullOrWhiteSpace(agencyId))
        {
            EmitError("run-agency requer --organization-id <id-do-orgao-no-catalogo>");
            return 2;
        }

        var home = options.TryGetValue("home", out var configuredHome)
            ? configuredHome
            : Environment.GetEnvironmentVariable("GOVDATA_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(AppContext.BaseDirectory, "data");
        try { home = Path.GetFullPath(home); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            EmitError($"GOVDATA_HOME inválido: {ex.Message}");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        DownloadManager? downloads = null;
        DownloadAutomation? automation = null;
        HttpClient? http = null;
        FileStream? ownership = null;
        try
        {
            var catalogPath = Path.Combine(home, "catalogo.json");
            if (!File.Exists(catalogPath))
                throw new FileNotFoundException("Catálogo v2 não encontrado em GOVDATA_HOME.", catalogPath);

            var originsPath = Path.Combine(home, "server", "download-origins.json");
            if (!File.Exists(originsPath)) originsPath = Path.Combine(AppContext.BaseDirectory, "download-origins.json");
            if (!File.Exists(originsPath))
                throw new FileNotFoundException("Configuração de origens autorizadas não encontrada.", originsPath);

            ownership = DownloadStateOwner.Acquire(home);
            var originPolicy = DownloadOriginPolicy.Load(originsPath);
            http = originPolicy.CreateHttpClient();
            var catalog = new CatalogStore(catalogPath);
            var settings = new AppSettingsStore(Path.Combine(home, "settings.json"));
            downloads = new DownloadManager(http, Path.Combine(home, "downloads.json"), TimeProvider.System, originPolicy);
            automation = new DownloadAutomation(catalog, downloads, settings);

            Emit("downloads.started", "RUNNING", "Iniciando reconciliação incremental do órgão.", agencyId, "download");
            string? lastSnapshot = null;
            while (!cancellation.IsCancellationRequested)
            {
                await automation.ReconcileAgencyAsync(agencyId, cancellation.Token);
                var snapshot = automation.Snapshot;
                if (snapshot.LastError is { Length: > 0 } error)
                    throw new InvalidOperationException(error);

                var fingerprint = $"{snapshot.Sources}:{snapshot.UpToDate}:{snapshot.Pending}:{snapshot.Attention}:{snapshot.NextRetryUtc:O}";
                if (!string.Equals(lastSnapshot, fingerprint, StringComparison.Ordinal))
                {
                    Emit("downloads.progress", "RUNNING", "Estado da fila atualizado.", agencyId, "download",
                        snapshot.UpToDate, snapshot.Sources,
                        new { pending = snapshot.Pending, attention = snapshot.Attention, nextRetryUtc = snapshot.NextRetryUtc });
                    lastSnapshot = fingerprint;
                }

                if (snapshot.Pending == 0 && snapshot.UpToDate == snapshot.Sources && snapshot.Attention == 0)
                {
                    Emit("downloads.completed", "SUCCEEDED", "Todos os arquivos do órgão têm recibos atuais.",
                        agencyId, "download", snapshot.Sources, snapshot.Sources,
                        new { pending = 0, attention = 0 });
                    return 0;
                }

                if (snapshot.Pending == 0 && snapshot.Attention > 0 && snapshot.NextRetryUtc is null)
                {
                    Emit("downloads.needs_attention", "BLOCKED", "Há downloads pausados ou falhos que exigem ação.",
                        agencyId, "download", snapshot.UpToDate, snapshot.Sources,
                        new { pending = 0, attention = snapshot.Attention });
                    return 3;
                }

                var delay = snapshot.Pending > 0
                    ? TimeSpan.FromSeconds(2)
                    : snapshot.NextRetryUtc is { } retry
                        ? TimeSpan.FromSeconds(Math.Clamp((retry - DateTimeOffset.UtcNow).TotalSeconds, 1, 30))
                        : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, cancellation.Token);
            }

            return 130;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Emit("downloads.stopping", "CANCELLED", "Execução interrompida; estado retomável foi preservado.", agencyId, "download");
            return 130;
        }
        catch (Exception ex)
        {
            EmitError(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (downloads is not null)
            {
                try { await downloads.StopAsync(CancellationToken.None); }
                catch (Exception ex) { EmitError($"Falha ao concluir o estado dos downloads: {ex.Message}"); }
                downloads.Dispose();
            }
            automation?.Dispose();
            http?.Dispose();
            ownership?.Dispose();
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Argumento inválido: {args[i]}");
            result[args[i][2..]] = args[++i];
        }
        var unknown = result.Keys.FirstOrDefault(key => key is not ("organization-id" or "home"));
        if (unknown is not null) throw new ArgumentException($"Opção desconhecida: --{unknown}");
        return result;
    }

    private static void Emit(string eventType, string status, string message, string? organizationId,
        string stage, int? progressCurrent = null, int? progressTotal = null, object? details = null)
    {
        var value = new
        {
            schemaVersion = 1,
            eventId = Guid.NewGuid(),
            occurredAt = DateTimeOffset.UtcNow,
            component = "downloads",
            eventType,
            severity = status == "BLOCKED" ? "warning" : "info",
            status,
            stage,
            organizationId,
            progressCurrent,
            progressTotal,
            message,
            details = details ?? new { }
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(value, EventJson));
    }

    private static void EmitError(string message) => Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        eventId = Guid.NewGuid(),
        occurredAt = DateTimeOffset.UtcNow,
        component = "downloads",
        eventType = "downloads.worker_failed",
        severity = "error",
        status = "FAILED",
        stage = "worker",
        message,
        details = new { }
    }, EventJson));

    private static void PrintUsage() =>
        Console.Error.WriteLine("Uso: GovData.Downloads.Worker.Cli run [--home <GOVDATA_HOME>] | status [--home <GOVDATA_HOME>] | control <check-now|pause-auto|resume-auto|pause-job|resume-job> [--job-id <uuid>] [--home <GOVDATA_HOME>] | run-agency --organization-id <id-do-orgao-no-catalogo> [--home <GOVDATA_HOME>]");
}

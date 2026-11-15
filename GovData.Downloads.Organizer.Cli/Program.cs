using System.Text.Json;
using System.Text.Json.Serialization;
using GovData.Downloads.Core;
using GovData.Downloads.Server;

return await OrganizerProgram.RunAsync(args);

internal static class OrganizerProgram
{
    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] is "list" or "plan" or "organize" or "amostrar")
            return await ManualOrganizer.RunAsync(args);
        if (args.Length > 0 && args[0] == "espaco")
        {
            // Diagnóstico da proteção de disco: qual volume cada caminho usa e quanto sobra.
            foreach (var path in args.Length > 1 ? args.Skip(1) : [Environment.CurrentDirectory])
            {
                var c = StorageProtection.ReadCapacity(path);
                Console.WriteLine($"{path} -> volume {c.Volume}: livre {c.AvailableBytes / 1e9:N1} GB de {c.TotalBytes / 1e9:N1} GB " +
                    $"({100d * c.AvailableBytes / Math.Max(1, c.TotalBytes):N1}%); mínimo exigido {StorageProtection.MinimumFreePercent}%.");
            }
            return 0;
        }
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }
        if (!string.Equals(args[0], "run-agency", StringComparison.Ordinal))
        {
            EmitError(null, $"Comando desconhecido: {args[0]}");
            PrintUsage();
            return 2;
        }

        Dictionary<string, string?> options;
        try { options = ParseOptions(args.Skip(1).ToArray()); }
        catch (ArgumentException ex)
        {
            EmitError(null, ex.Message);
            return 2;
        }
        if (!options.TryGetValue("organization-id", out var organizationId) || string.IsNullOrWhiteSpace(organizationId))
        {
            EmitError(null, "run-agency requer --organization-id <id-do-orgao-no-catalogo>.");
            return 2;
        }
        var home = options.GetValueOrDefault("home") ?? Environment.GetEnvironmentVariable("GOVDATA_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(AppContext.BaseDirectory, "data");
        try { home = Path.GetFullPath(home); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            EmitError(organizationId, $"GOVDATA_HOME inválido: {ex.Message}");
            return 2;
        }
        var force = options.ContainsKey("force");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var serverHome = Path.Combine(home, "server");
            var settingsPath = Path.Combine(serverHome, "settings.json");
            if (!File.Exists(settingsPath))
                throw new FileNotFoundException("Configuração do organizador não encontrada.", settingsPath);

            var settings = JsonSerializer.Deserialize<ServerOptions>(
                await File.ReadAllTextAsync(settingsPath, cancellation.Token), JsonStorage.Options)
                ?? throw new InvalidDataException("Configuração do organizador vazia.");
            settings.ValidateProcessingOptions();
            settings.CatalogPath = Path.GetFullPath(settings.CatalogPath ?? Path.Combine(home, "catalogo.json"));
            settings.ConjuntosPath = Path.GetFullPath(settings.ConjuntosPath ?? Path.Combine(serverHome, "conjuntos"));
            settings.StorageRoot = Path.GetFullPath(settings.StorageRoot ?? Path.Combine(serverHome, "storage"));
            var appSettings = await new AppSettingsStore(Path.Combine(home, "settings.json"))
                .LoadAsync(cancellation.Token);
            settings.DownloadRoot = Path.GetFullPath(settings.DownloadRoot ??
                (string.IsNullOrWhiteSpace(appSettings.DownloadRoot) ? Path.Combine(home, "downloads") : appSettings.DownloadRoot));

            using var ownership = PublicationStateOwner.Acquire(settings.StorageRoot);
            var engine = new PublicationEngine(settings, includeApiCompatibilityArtifacts: false);
            await engine.InitializeAsync(cancellation.Token);
            Emit("organizer.started", "RUNNING", "Organização incremental do órgão iniciada.", organizationId, "organize");
            var readyReleases = await engine.ReconcileAgencyAsync(organizationId, cancellation.Token, force);
            foreach (var release in readyReleases)
                Emit("organizer.publication_available", "SUCCEEDED", "Publicação imutável pronta para cadastro no Bronze.",
                    organizationId, "publish", release.Id,
                    new { conjuntoId = release.ConjuntoId, partitions = release.Partitions.Count,
                        rows = release.Partitions.Sum(part => part.Rows) });

            if (engine.LastError is { Length: > 0 } error)
            {
                EmitError(organizationId, error);
                return 1;
            }

            Emit("organizer.completed", "SUCCEEDED", "Reconciliação do órgão concluída.", organizationId,
                "organize", details: new { availablePublications = readyReleases.Length,
                    publicationIds = readyReleases.Select(release => release.Id).ToArray() });
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Emit("organizer.stopping", "CANCELLED", "Organização interrompida; o estado publicado foi preservado.",
                organizationId, "organize");
            return 130;
        }
        catch (Exception ex)
        {
            EmitError(organizationId, ex.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--force")
            {
                if (!result.TryAdd("force", null)) throw new ArgumentException("--force repetido.");
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length ||
                args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Argumento inválido: {args[i]}");
            var key = args[i][2..];
            if (key is not ("organization-id" or "home")) throw new ArgumentException($"Opção desconhecida: --{key}");
            if (!result.TryAdd(key, args[++i])) throw new ArgumentException($"--{key} repetido.");
        }
        return result;
    }

    private static void Emit(string eventType, string status, string message, string? organizationId,
        string stage, string? publicationId = null, object? details = null) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventId = Guid.NewGuid(),
            occurredAt = DateTimeOffset.UtcNow,
            component = "organizer",
            eventType,
            severity = "info",
            status,
            stage,
            organizationId,
            publicationId,
            message,
            details = details ?? new { }
        }, EventJson));

    private static void EmitError(string? organizationId, string message) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventId = Guid.NewGuid(),
            occurredAt = DateTimeOffset.UtcNow,
            component = "organizer",
            eventType = "organizer.failed",
            severity = "error",
            status = "FAILED",
            stage = "organize",
            organizationId,
            message,
            details = new { }
        }, EventJson));

    private static void PrintUsage() =>
        Console.Error.WriteLine("Uso: GovData.Downloads.Organizer.Cli <list|plan|organize> [--organization-id ID] [--function-id ID] [--output DIR] [--config FILE] [--home DIR] [--duckdb-memory-mb MIB] [--duckdb-threads N] [--capture-batch-rows N] [--massive-threshold-mb MIB] [--progress-interval-seconds N] [--force] [--recapture-grid] [--allow-unmapped]\n" +
            "Amostra: amostrar --organization-id ID --function-id ID [--source PADRAO] [--rows N] [--encoding ENC] [--delimiter D] [--member-format csv]\n" +
            "Comando legado: run-agency --organization-id ID [--home DIR] [--force].");
}

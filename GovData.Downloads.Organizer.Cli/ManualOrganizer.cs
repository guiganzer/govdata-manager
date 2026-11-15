using System.Text.Json;
using System.Security.Cryptography;
using GovData.Downloads.Core;
using GovData.Downloads.Server;

internal sealed class ManualOrganizerConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string? OrganizationId { get; set; }
    public string? FunctionId { get; set; }
    public string OutputRoot { get; set; } = "organization-output";
    public bool RequireAllFiles { get; set; } = true;
    public bool Force { get; set; }
    public string[] ConjuntoPatterns { get; set; } = ["*"];
    public int? DuckDbMemoryMb { get; set; }
    public int? DuckDbThreads { get; set; }
    public int? CaptureBatchRows { get; set; }
    public int? MassiveCsvThresholdMb { get; set; }
    public bool RecaptureGrid { get; set; }
    public int ProgressIntervalSeconds { get; set; } = 5;
}

internal static class ManualOrganizer
{
    private sealed record Source(Orgao Agency, Funcao Function, ArquivoCatalogo File, string Path);

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = Parse(args.Skip(1).ToArray());
            var home = Path.GetFullPath(options.GetValueOrDefault("home")
                ?? Environment.GetEnvironmentVariable("GOVDATA_HOME")
                ?? throw new ArgumentException("Defina GOVDATA_HOME ou --home."));
            var configPath = Path.GetFullPath(options.GetValueOrDefault("config")
                ?? Path.Combine(home, "organizer.json"));
            var config = JsonSerializer.Deserialize<ManualOrganizerConfig>(
                await File.ReadAllTextAsync(configPath, cancellation.Token), JsonStorage.Options)
                ?? throw new InvalidDataException("Configuração do organizador vazia.");
            if (config.SchemaVersion != 1 || string.IsNullOrWhiteSpace(config.OutputRoot) ||
                config.ConjuntoPatterns is not { Length: > 0 })
                throw new InvalidDataException("organizer.json inválido: schemaVersion, outputRoot ou conjuntoPatterns.");
            var progressIntervalSeconds = config.ProgressIntervalSeconds;
            if (options.TryGetValue("progress-interval-seconds", out var intervalText))
            {
                if (!int.TryParse(intervalText, out progressIntervalSeconds))
                    throw new ArgumentException("--progress-interval-seconds deve ser um inteiro de 1 a 60.");
            }
            if (progressIntervalSeconds is < 1 or > 60)
                throw new ArgumentException("progressIntervalSeconds deve estar entre 1 e 60.");
            var agencyId = options.GetValueOrDefault("organization-id") ?? config.OrganizationId;
            var functionId = options.GetValueOrDefault("function-id") ?? config.FunctionId;
            if (args[0] != "list" && string.IsNullOrWhiteSpace(agencyId))
                throw new ArgumentException("Informe --organization-id ou organizationId em organizer.json.");
            if (agencyId is not null && (agencyId.IndexOfAny(['/', '\\']) >= 0 || agencyId is "." or ".."))
                throw new ArgumentException("ID do órgão não pode conter separadores de caminho.");
            if (functionId is not null && (functionId.IndexOfAny(['/', '\\']) >= 0 || functionId is "." or ".."))
                throw new ArgumentException("ID da função não pode conter separadores de caminho.");
            var defaultOutputRoot = Path.IsPathFullyQualified(config.OutputRoot)
                ? config.OutputRoot : Path.Combine(home, config.OutputRoot);
            var output = Path.GetFullPath(options.GetValueOrDefault("output")
                ?? Path.Combine(defaultOutputRoot, agencyId ?? "all", functionId ?? ""));
            var settingsPath = Path.Combine(home, "server", "settings.json");
            var settings = JsonSerializer.Deserialize<ServerOptions>(
                await File.ReadAllTextAsync(settingsPath, cancellation.Token), JsonStorage.Options)
                ?? throw new InvalidDataException("server/settings.json vazio.");
            if (config.DuckDbMemoryMb is { } configuredMemory)
                settings.DuckDbMemoryMb = configuredMemory;
            if (config.DuckDbThreads is { } configuredThreads)
                settings.DuckDbThreads = configuredThreads;
            if (config.CaptureBatchRows is { } configuredBatch)
                settings.CaptureBatchRows = configuredBatch;
            if (config.MassiveCsvThresholdMb is { } configuredThreshold)
                settings.MassiveCsvThresholdMb = configuredThreshold;
            if (options.TryGetValue("massive-threshold-mb", out var thresholdText))
            {
                if (!int.TryParse(thresholdText, out var thresholdMb))
                    throw new ArgumentException("--massive-threshold-mb deve ser um inteiro em MiB (0 desativa).");
                settings.MassiveCsvThresholdMb = thresholdMb;
            }
            settings.RecaptureGrid = config.RecaptureGrid || options.ContainsKey("recapture-grid");
            if (options.TryGetValue("duckdb-memory-mb", out var memoryText))
            {
                if (!int.TryParse(memoryText, out var memoryMb))
                    throw new ArgumentException("--duckdb-memory-mb deve ser um inteiro em MiB.");
                settings.DuckDbMemoryMb = memoryMb;
            }
            if (options.TryGetValue("duckdb-threads", out var threadsText))
            {
                if (!int.TryParse(threadsText, out var threads))
                    throw new ArgumentException("--duckdb-threads deve ser um inteiro.");
                settings.DuckDbThreads = threads;
            }
            if (options.TryGetValue("capture-batch-rows", out var batchText))
            {
                if (!int.TryParse(batchText, out var batchRows))
                    throw new ArgumentException("--capture-batch-rows deve ser um inteiro entre 100 e 65536.");
                settings.CaptureBatchRows = batchRows;
            }
            settings.AutomaticPublicationConjuntos = config.ConjuntoPatterns;
            settings.ValidateProcessingOptions();
            settings.CatalogPath = Path.GetFullPath(settings.CatalogPath ?? Path.Combine(home, "catalogo.json"));
            settings.ConjuntosPath = Path.GetFullPath(settings.ConjuntosPath ?? Path.Combine(home, "server", "conjuntos"));
            var appSettings = await new AppSettingsStore(Path.Combine(home, "settings.json"))
                .LoadAsync(cancellation.Token);
            settings.DownloadRoot = Path.GetFullPath(settings.DownloadRoot ??
                (string.IsNullOrWhiteSpace(appSettings.DownloadRoot) ? Path.Combine(home, "downloads") : appSettings.DownloadRoot));
            if (IsWithin(output, settings.DownloadRoot) || IsWithin(settings.DownloadRoot, output))
                throw new ArgumentException("A saída não pode coincidir com a pasta de downloads nem contê-la.");
            var existingStorage = Path.GetFullPath(settings.StorageRoot ?? Path.Combine(home, "server", "storage"));
            if (IsWithin(output, existingStorage) || IsWithin(existingStorage, output))
                throw new ArgumentException("A saída não pode coincidir com o armazenamento antigo nem contê-lo.");
            settings.StorageRoot = output;

            var catalog = await new CatalogStore(settings.CatalogPath).LoadAsync(cancellation.Token);
            if (args[0] == "amostrar")
                return await SampleAsync(settings, catalog, agencyId!, functionId, options, cancellation.Token);
            if (args[0] == "list")
            {
                await ListAsync(settings, catalog, options, cancellation.Token);
                return 0;
            }
            var agency = catalog.Orgaos.SingleOrDefault(x =>
                string.Equals(x.Id, agencyId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Órgão não encontrado no catálogo v2: {agencyId}");
            var selectedFunction = functionId is null ? null : agency.Funcoes.SingleOrDefault(f =>
                string.Equals(f.Id, functionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Função {functionId} não existe no órgão {agency.Id}.");
            var functions = selectedFunction is null ? agency.Funcoes : [selectedFunction];
            var sources = functions.SelectMany(f => f.Arquivos.Select(a =>
                new Source(agency, f, a, DownloadPaths.ForFile(settings.DownloadRoot, agency, f, a)))).ToArray();
            var sourceIds = sources.Select(x => x.File.Id).ToHashSet(StringComparer.Ordinal);
            var engine = new PublicationEngine(settings, includeApiCompatibilityArtifacts: false);
            await engine.LoadManifestAsync(cancellation.Token);
            var definitions = (await engine.SnapshotAsync(cancellation.Token)).Definitions
                .Where(d => settings.AllowsAutomaticPublication(d.ConjuntoId) &&
                    d.Partitions.Any(p => sourceIds.Contains(p.ArquivoId))).ToArray();
            var crossAgency = definitions.Where(d => d.Partitions.Any(p => !sourceIds.Contains(p.ArquivoId)))
                .Select(d => d.Id).ToArray();
            var covered = definitions.SelectMany(d => d.Partitions).Select(p => p.ArquivoId)
                .ToHashSet(StringComparer.Ordinal);
            var documents = sources.Where(x => IsDocument(x.File)).Select(x =>
                new { id = x.File.Id, name = x.File.Nome, file = x.File.NomeArquivo }).ToArray();
            var unmapped = sources.Where(x => !IsDocument(x.File) && !covered.Contains(x.File.Id)).Select(x =>
                new { id = x.File.Id, name = x.File.Nome, file = x.File.NomeArquivo }).ToArray();
            var receiptPath = Path.Combine(Path.GetDirectoryName(settings.CatalogPath)!, "downloads-receipts.json");
            var receipts = JsonSerializer.Deserialize<DownloadReceipt[]>(
                await File.ReadAllTextAsync(receiptPath, cancellation.Token), JsonStorage.Options) ?? [];
            var byPath = receipts.GroupBy(x => x.Destino, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            var missing = sources.Where(x => !byPath.TryGetValue(x.Path, out var receipt) ||
                !ReceiptMatches(receipt, x)).Select(x =>
                    new { id = x.File.Id, name = x.File.Nome, file = x.File.NomeArquivo }).ToArray();
            var requireAll = config.RequireAllFiles && !options.ContainsKey("allow-unmapped");
            var ready = definitions.Length > 0 && crossAgency.Length == 0 && missing.Length == 0 &&
                (!requireAll || unmapped.Length == 0);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, command = args[0], organizationId = agency.Id,
                organizationName = agency.Nome, functionId = selectedFunction?.Id,
                functionName = selectedFunction?.Nome, output,
                sourceFiles = sources.Length, definitions = definitions.Length,
                documents, unmapped, missingDownloads = missing, crossScopeDefinitions = crossAgency,
                requireAllFiles = requireAll, ready
            }, JsonStorage.Options));
            if (args[0] == "plan") return ready ? 0 : 3;
            if (!ready) throw new InvalidOperationException(
                "Pré-validação reprovada; confira arquivos sem layout, downloads e definições entre órgãos.");

            using var ownership = PublicationStateOwner.Acquire(output);
            await engine.InitializeAsync(cancellation.Token);
            var force = config.Force || options.ContainsKey("force");
            Console.Error.WriteLine($"Organizando {agency.Nome}" +
                (selectedFunction is null ? "" : $" / {selectedFunction.Nome}") +
                $": {definitions.Length} definição(ões); saída {output}.");
            Console.Error.WriteLine($"DuckDB: limite {DuckSql.MemoryLimit(settings.DuckDbMemoryMb)}; " +
                $"threads {settings.DuckDbThreads}; lote {settings.CaptureBatchRows} linhas; " +
                (settings.MassiveCsvThresholdMb > 0
                    ? $"modo massivo para CSV a partir de {settings.MassiveCsvThresholdMb} MiB."
                    : "modo massivo desativado."));
            using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            var progressTask = ReportProgressAsync(engine.Progress, progressIntervalSeconds,
                progressCancellation.Token);
            PublishedRelease[] releases;
            try
            {
                releases = selectedFunction is null
                    ? await engine.ReconcileAgencyAsync(agency.Id, cancellation.Token, force, recheckExisting: true)
                    : await engine.ReconcileFunctionAsync(agency.Id, selectedFunction.Id, cancellation.Token,
                        force, recheckExisting: true);
            }
            finally
            {
                await progressCancellation.CancelAsync();
                await progressTask;
            }
            if (engine.LastError is { Length: > 0 } error)
                throw new InvalidOperationException(error);
            var chosen = definitions.Select(d => releases.Where(r => r.PurgedAt is null && r.DefinitionId == d.Id)
                .OrderByDescending(r => r.PublishedAt).FirstOrDefault()).ToArray();
            if (chosen.Any(x => x is null))
                throw new InvalidOperationException("Nem todas as definições do órgão produziram uma publicação.");
            var publicationIds = chosen.Select(x => x!.Id).Distinct(StringComparer.Ordinal).ToArray();
            var documentRecords = new List<object>();
            foreach (var source in sources.Where(x => IsDocument(x.File)))
                documentRecords.Add(await CopyDocumentAsync(source, byPath[source.Path], output, cancellation.Token));
            var result = new
            {
                schemaVersion = 1, status = "READY", organizationId = agency.Id,
                organizationName = agency.Nome, functionId = selectedFunction?.Id,
                functionName = selectedFunction?.Nome, completedAt = DateTimeOffset.UtcNow,
                sourceFiles = sources.Length, mappedFiles = covered.Count,
                documents = documentRecords, unmappedFiles = unmapped, publicationIds,
                publications = chosen.Select(x => new
                {
                    id = x!.Id, x.DefinitionId, x.ConjuntoId, x.VersaoDefinicao,
                    partitions = x.Partitions.Select(p => new
                    {
                        id = p.ArquivoId, p.Rows, p.SchemaHash, p.OriginalSha256,
                        parquet = Path.Combine("releases", x.Id, p.ArquivoId, p.ParquetFile)
                    }).ToArray()
                }).ToArray()
            };
            await JsonStorage.SaveAsync(Path.Combine(output, "organization-run.json"), result, cancellation.Token);
            Console.Error.WriteLine($"Organização concluída: {publicationIds.Length} publicação(ões), " +
                $"{chosen.Sum(x => x!.Partitions.Sum(p => p.Rows)):N0} linhas; manifesto {output}/organization-run.json.");
            Console.WriteLine(JsonSerializer.Serialize(result, JsonStorage.Options));
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task ReportProgressAsync(PublicationProgress progress, int intervalSeconds,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        var idleTicks = 0;
        var lastPartition = "";
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (progress.Get() is not { } current)
                {
                    if (++idleTicks >= Math.Max(1, 30 / intervalSeconds))
                    {
                        Console.Error.WriteLine("Organização ativa; aguardando a próxima definição ou conclusão.");
                        Console.Error.Flush();
                        idleTicks = 0;
                    }
                    continue;
                }

                idleTicks = 0;
                var partition = $"{current.ConjuntoId}/{current.ArquivoId}";
                if (partition != lastPartition)
                {
                    Console.Error.WriteLine($"Partição {current.Partition}/{current.Partitions}: {partition}");
                    lastPartition = partition;
                }
                var elapsed = DateTimeOffset.UtcNow - current.StageStarted;
                var unit = current.Stage.StartsWith("Copiando", StringComparison.OrdinalIgnoreCase)
                    ? " bytes" : current.Stage == "Capturando grade" ? " linhas" : "";
                var amount = current.Total > 0
                    ? $" | {current.Done:N0}/{current.Total:N0}{unit} " +
                      $"({Math.Min(100, 100.0 * current.Done / current.Total):F1}%)"
                    : current.Done > 0 ? $" | {current.Done:N0}{unit}" : "";
                Console.Error.WriteLine($"  {current.Stage}{amount} | etapa {elapsed:hh\\:mm\\:ss}");
                Console.Error.Flush();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task ListAsync(ServerOptions settings, Catalogo catalog,
        Dictionary<string, string?> options, CancellationToken ct)
    {
        var engine = new PublicationEngine(settings, includeApiCompatibilityArtifacts: false);
        await engine.LoadManifestAsync(ct);
        var definitions = (await engine.SnapshotAsync(ct)).Definitions
            .Where(d => settings.AllowsAutomaticPublication(d.ConjuntoId)).ToArray();
        var mapped = definitions.SelectMany(d => d.Partitions).Select(p => p.ArquivoId)
            .ToHashSet(StringComparer.Ordinal);
        var receiptPath = Path.Combine(Path.GetDirectoryName(settings.CatalogPath!)!, "downloads-receipts.json");
        var receipts = JsonSerializer.Deserialize<DownloadReceipt[]>(
            await File.ReadAllTextAsync(receiptPath, ct), JsonStorage.Options) ?? [];
        var byPath = receipts.GroupBy(x => x.Destino, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var agencies = catalog.Orgaos.Where(a => options.GetValueOrDefault("organization-id") is not { } id ||
            string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (agencies.Length == 0) throw new KeyNotFoundException("Órgão não encontrado no catálogo v2.");
        var shown = 0;
        foreach (var agency in agencies)
        {
            var functions = agency.Funcoes.Where(f => options.GetValueOrDefault("function-id") is not { } id ||
                string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (functions.Length == 0) continue;
            Console.WriteLine($"{agency.Nome} [{agency.Id}]");
            shown++;
            foreach (var function in functions)
            {
                var sources = function.Arquivos.Select(file => new Source(agency, function, file,
                    DownloadPaths.ForFile(settings.DownloadRoot!, agency, function, file))).ToArray();
                var ready = sources.Count(source => byPath.TryGetValue(source.Path, out var receipt) &&
                    ReceiptMatches(receipt, source));
                var layouts = sources.Count(source => mapped.Contains(source.File.Id));
                var documents = sources.Count(source => IsDocument(source.File));
                var marker = ready == sources.Length && layouts + documents == sources.Length && sources.Length > 0
                    ? "PRONTA" : "PENDENTE";
                Console.WriteLine($"  └─ {function.Nome} [{function.Id}]  downloads {ready}/{sources.Length}  V3 {layouts}/{sources.Length - documents}  documentos {documents}  {marker}");
            }
        }
        if (shown == 0) throw new KeyNotFoundException("Função não encontrada no catálogo v2.");
    }

    /// <summary>
    /// Mostra as primeiras linhas de cada membro/aba de cada arquivo da função, usando o
    /// mesmo leitor do motor (CSV, ZIP, 7z, XLSX, XLS, DBF, DBC). Só lê; não grava nada.
    /// Saída: uma linha JSON por membro/aba, para escrever layouts V3.
    /// </summary>
    private static async Task<int> SampleAsync(ServerOptions settings, Catalogo catalog, string agencyId,
        string? functionId, Dictionary<string, string?> options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(functionId)) throw new ArgumentException("amostrar exige --function-id.");
        var agency = catalog.Orgaos.Single(x => string.Equals(x.Id, agencyId, StringComparison.OrdinalIgnoreCase));
        var function = agency.Funcoes.SingleOrDefault(f => string.Equals(f.Id, functionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Função {functionId} não existe no órgão {agency.Id}.");
        var rows = int.TryParse(options.GetValueOrDefault("rows") ?? "3", out var r) && r is >= 1 and <= 20 ? r
            : throw new ArgumentException("--rows deve ser de 1 a 20.");
        var pattern = options.GetValueOrDefault("source") ?? "*";
        var encoding = options.GetValueOrDefault("encoding") ?? "windows-1252";
        var delimiter = options.GetValueOrDefault("delimiter") ?? ";";
        var json = new JsonSerializerOptions(JsonStorage.Options) { WriteIndented = false };
        void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value, json)); Console.Out.Flush(); }
        foreach (var file in function.Arquivos.Where(a => ConjuntoContract.Matches(a.Id, [pattern])))
        {
            ct.ThrowIfCancellationRequested();
            if (IsDocument(file)) continue;
            var path = DownloadPaths.ForFile(settings.DownloadRoot!, agency, function, file);
            var format = Path.GetExtension(file.NomeArquivo).TrimStart('.').ToLowerInvariant();
            if (!File.Exists(path)) { Emit(new { source = file.Id, file = file.NomeArquivo, error = "download ausente" }); continue; }
            // ZIP: cada membro é lido isoladamente, para não percorrer membros grandes inteiros.
            var members = new List<string?>();
            if (format == "zip")
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                members.AddRange(zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => (string?)e.FullName)
                    .Order(StringComparer.Ordinal));
            }
            else members.Add(null);
            foreach (var member in members)
            {
                var source = new ConjuntoSources
                {
                    Funcao = function.Id, Arquivos = [file.Id], Encoding = encoding, Delimiter = delimiter,
                    Membros = member is null ? null : [member],
                    FormatoMembro = options.GetValueOrDefault("member-format")
                };
                var groups = new Dictionary<(string?, string?), List<IReadOnlyList<string?>>>();
                try
                {
                    foreach (var row in RawGrid.Read(path, format, source, ct))
                    {
                        if (row.Cells.All(string.IsNullOrEmpty)) continue;
                        var key = (row.Member ?? member, row.Sheet);
                        if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                        if (list.Count < rows) list.Add(row.Cells);
                        // Texto em fluxo (CSV, DBF, 7z): basta o início do primeiro membro.
                        else if (row.Sheet is null) break;
                    }
                    if (groups.Count == 0 && member is not null)
                        Emit(new { source = file.Id, file = file.NomeArquivo, member, note = "membro ignorado pelo leitor (formato não tabular)" });
                    else if (groups.Count == 0 && format == "7z")
                    {
                        // Nenhum membro com extensão tabular: lista os nomes para escolher --member-format.
                        using var archive = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(path,
                            new SharpCompress.Readers.ReaderOptions());
                        Emit(new { source = file.Id, file = file.NomeArquivo,
                            note = "nenhum membro lido; repita com --member-format csv se forem texto",
                            members = archive.Entries.Where(e => !e.IsDirectory).Select(e => new { name = e.Key, size = e.Size }).ToArray() });
                    }
                    else if (groups.Count == 0)
                        Emit(new { source = file.Id, file = file.NomeArquivo, note = "nenhuma linha lida" });
                    foreach (var ((m, sheet), list) in groups)
                        Emit(new { source = file.Id, file = file.NomeArquivo, member = m, sheet,
                            width = list.Max(x => x.Count), rows = list });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Emit(new { source = file.Id, file = file.NomeArquivo, member, error = ex.Message });
                    if (format == "7z") Emit(SevenZipHead(file.Id, path));
                }
            }
        }
        await Task.CompletedTask;
        return 0;
    }

    /// <summary>Diagnóstico de 7z ilegível: membros e os primeiros bytes brutos de cada um (texto escapado).</summary>
    private static object SevenZipHead(string sourceId, string path)
    {
        using var archive = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(path,
            new SharpCompress.Readers.ReaderOptions());
        using var reader = archive.ExtractAllEntries();
        var members = new List<object>();
        while (reader.MoveToNextEntry() && members.Count < 10)
        {
            if (reader.Entry.IsDirectory) continue;
            var head = new byte[400];
            int read = 0, n;
            using (var s = reader.OpenEntryStream())
            {
                while (read < head.Length && (n = s.Read(head, read, head.Length - read)) > 0) read += n;
            }
            var text = string.Concat(head.Take(read).Select(b =>
                b is >= 0x20 and < 0x7f && b != '\\' ? ((char)b).ToString() : $"\\x{b:x2}"));
            members.Add(new { name = reader.Entry.Key, size = reader.Entry.Size, head = text });
        }
        return new { source = sourceId, diagnostic = "inicio bruto dos membros do 7z", members };
    }

    private static bool ReceiptMatches(DownloadReceipt receipt, Source source)
    {
        var info = new FileInfo(source.Path);
        return info.Exists && info.Length == receipt.TamanhoLocalBytes &&
            info.LastWriteTimeUtc == receipt.ModificadoEmUtc.UtcDateTime &&
            receipt.ArquivoId == source.File.Id && receipt.Url == source.File.Url &&
            receipt.Competencia == source.File.Competencia &&
            receipt.Substituivel == source.File.Substituivel;
    }

    private static bool IsDocument(ArquivoCatalogo file) =>
        Path.GetExtension(file.NomeArquivo).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static async Task<object> CopyDocumentAsync(Source source, DownloadReceipt receipt,
        string output, CancellationToken ct)
    {
        if (File.GetAttributes(source.Path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"Documento com link simbólico: {source.File.Id}");
        var folder = Path.Combine(output, "documents", source.File.Id);
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Path.GetFileName(source.Path));
        var temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = File.OpenRead(source.Path))
            await using (var copy = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(copy, ct);
                await copy.FlushAsync(ct);
            }
            await using var verification = File.OpenRead(temporary);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(verification, ct)).ToLowerInvariant();
            var bytes = new FileInfo(temporary).Length;
            if (!ReceiptMatches(receipt, source) || receipt.TamanhoLocalBytes != bytes ||
                source.File.Sha256 is { } expected &&
                !sha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Documento mudou durante a cópia: {source.File.Id}");
            File.Move(temporary, destination, true);
            return new
            {
                id = source.File.Id, bytes, sha256,
                path = Path.Combine("documents", source.File.Id, Path.GetFileName(source.Path))
            };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--force" or "--allow-unmapped" or "--recapture-grid")
            {
                if (!result.TryAdd(key[2..], null)) throw new ArgumentException($"{key} repetido.");
                continue;
            }
            if (key is not ("--home" or "--config" or "--organization-id" or "--function-id" or "--output" or
                "--duckdb-memory-mb" or "--duckdb-threads" or "--capture-batch-rows" or
                "--progress-interval-seconds" or "--massive-threshold-mb" or
                "--source" or "--rows" or "--encoding" or "--delimiter" or "--member-format") ||
                i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Argumento inválido: {key}");
            if (!result.TryAdd(key[2..], args[++i])) throw new ArgumentException($"{key} repetido.");
        }
        return result;
    }
}

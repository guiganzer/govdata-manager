using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GovData.Downloads.Core;
using GovData.Downloads.Server;

namespace GovData.Downloads.Desktop.Flow;

public sealed record SourceProfile(string Id, string Name, string Agency, string Url, string FileName,
    string[] Columns, string Delimiter = ";", string Encoding = "utf-8", bool IbgeTemplate = false)
{
    public override string ToString() => Name;
    public static SourceProfile Example => new("ibge-censo-2022", "IBGE • Municípios do Censo 2022 (326 KB)", "IBGE",
        "https://ftp.ibge.gov.br/Censos/Censo_Demografico_2022/Agregados_por_Setores_Censitarios/Agregados_por_Municipio_csv/Agregados_por_municipios_basico_BR_20260520.zip",
        "ibge_censo_2022_municipios_basico_20260520.zip", [], IbgeTemplate: true);
}
public sealed record FlowUpdate(string Stage, string Message, double? Fraction = null);
public sealed record FlowResult(string Folder, long Rows, string[] ParquetFiles, string Preview, string Manifest);

public static class GuidedFlow
{
    public static string DuckPath(string path) => OperatingSystem.IsWindows() && !path.StartsWith(@"\\?\")
        ? path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..] : @"\\?\" + Path.GetFullPath(path)
        : path;
    public static void Validate(SourceProfile source)
    {
        if (!Regex.IsMatch(source.Id, "^[a-z0-9][a-z0-9-]{0,60}$") || string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Agency))
            throw new ArgumentException("Informe um nome para a fonte e para o órgão responsável.");
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("Use um link HTTPS direto, sem usuário, senha, parâmetros ou fragmento.");
        Policy(source).Validate(uri);
        if (source.IbgeTemplate)
        {
            if (source != SourceProfile.Example && (source.Url != SourceProfile.Example.Url || source.FileName != SourceProfile.Example.FileName || source.Id != SourceProfile.Example.Id))
                throw new ArgumentException("A fonte pronta do IBGE não pode ser alterada. Cadastre outra fonte CSV.");
            return;
        }
        if (!Regex.IsMatch(source.FileName, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,60}\\.csv$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Use um nome simples terminado em .csv, por exemplo municipios.csv.");
        if (source.Columns.Length is < 1 or > 80 || source.Columns.Any(x => !Regex.IsMatch(x, "^[a-zA-Z][a-zA-Z0-9_]{0,60}$")) ||
            source.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Columns.Length)
            throw new ArgumentException("Informe de 1 a 80 colunas sem repetição, usando letras sem acento, números e sublinhado. Copie os nomes do cabeçalho CSV.");
        if (source.Delimiter is not ("," or ";" or "\t") || source.Encoding is not ("utf-8" or "windows-1252"))
            throw new ArgumentException("Escolha o separador e a codificação nas opções disponíveis.");
    }

    private static DownloadOriginPolicy Policy(SourceProfile source)
    {
        var uri = new Uri(source.Url);
        return new([new DownloadOriginRule { Scheme = "https", Host = uri.IdnHost,
            PathPrefix = uri.AbsolutePath[..(uri.AbsolutePath.LastIndexOf('/') + 1)] }]);
    }

    public static async Task SaveProfilesAsync(string path, IEnumerable<SourceProfile> sources)
    {
        var all = sources.ToArray(); foreach (var source in all) Validate(source);
        await JsonStorage.SaveAsync(path, all);
    }

    public static Task<FlowResult> RunAsync(SourceProfile source, string destination, IProgress<FlowUpdate> progress, CancellationToken ct)
        => RunCoreAsync(source, destination, progress, ct, null);

    internal static async Task<FlowResult> RunCoreAsync(SourceProfile source, string destination, IProgress<FlowUpdate> progress, CancellationToken ct, HttpClient? testClient)
    {
        Validate(source);
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Escolha uma pasta de destino completa.");
        var home = Path.Combine(Path.GetFullPath(destination), "GovData", source.Id);
        Directory.CreateDirectory(home);
        using var owner = DownloadStateOwner.Acquire(home);
        var storage = Path.Combine(home, "organizados");
        Directory.CreateDirectory(storage);
        using var publicationOwner = PublicationStateOwner.Acquire(storage);
        var downloadsRoot = Path.Combine(home, "originais");
        var definitions = Path.Combine(home, "conjuntos");
        Directory.CreateDirectory(definitions);
        var file = new ArquivoCatalogo { Id = source.IbgeTemplate ? "ibge-censo-2022-agregados-municipios-basico-20260520" : source.Id,
            Nome = source.Name, Url = source.Url, NomeArquivo = source.FileName };
        var function = new Funcao { Id = source.IbgeTemplate ? "ibge-censo-demografico-2022" : "csv-guiado", Nome = "Dados selecionados", Arquivos = [file] };
        var agency = new Orgao { Id = "fonte-guiada", Nome = source.Agency, Funcoes = [function] };
        var catalogPath = Path.Combine(home, "catalogo.json");
        await new CatalogStore(catalogPath).SaveAsync(new Catalogo { Orgaos = [agency] }, ct);
        string definitionText;
        if (source.IbgeTemplate)
        {
            using var stream = typeof(GuidedFlow).Assembly.GetManifestResourceStream("ibge.json")!;
            using var reader = new StreamReader(stream); definitionText = await reader.ReadToEndAsync(ct);
        }
        else
        {
            var definition = new ConjuntoDefinition { Conjunto = source.Id, Nome = source.Name, VersaoDefinicao = 1,
                Tipo = "ORGANIZADO", EdicaoPor = "ARQUIVO", Fontes = new() { Funcao = function.Id, Arquivos = [file.Id], Delimiter = source.Delimiter, Encoding = source.Encoding },
                Cabecalho = new() { ContemUmDe = [source.Columns[0]] },
                Colunas = source.Columns.ToDictionary(x => x.ToLowerInvariant(), x => new[] { x }),
                Saida = source.Columns.Select(x => x.ToLowerInvariant()).ToArray(), Chave = [], FiltroLinhas = "TRUE",
                Validacoes = new() { LinhasMinPorAba = 1, ColunasPresentes = source.Columns.Select(x => x.ToLowerInvariant()).ToArray() } };
            var node = JsonNode.Parse(JsonSerializer.Serialize(definition, JsonStorage.Options))!;
            node["cabecalho"]!.AsObject().Remove("semCabecalho");
            definitionText = node.ToJsonString();
        }
        var definitionPath = Path.Combine(definitions, "fonte.json");
        await File.WriteAllTextAsync(definitionPath, definitionText, ct);
        ConjuntoContract.Read(definitionPath);
        var policy = Policy(source);
        using var http = testClient ?? policy.CreateHttpClient();
        using var manager = new DownloadManager(http, Path.Combine(home, "downloads.json"), TimeProvider.System, policy);
        manager.SetMaxConcurrentDownloads(1);
        var target = DownloadPaths.ForFile(downloadsRoot, agency, function, file);
        try
        {
            await manager.InitializeAsync(resumeAutomatic: false, resumePending: false);
            var previous = manager.Jobs.FirstOrDefault(j => j.Destino == target && j.Status is DownloadStatus.Pausado or DownloadStatus.Falhou);
            DownloadJob job;
            if (previous is not null) { await manager.ResumeAsync(previous.Id); job = previous; }
            else job = await manager.EnqueueAsync(file, target);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var current = manager.Jobs.Single(j => j.Id == job.Id);
                progress.Report(new("Baixando", $"{current.BytesBaixados:N0} bytes recebidos de {new Uri(source.Url).Host}",
                    current.TotalBytes is > 0 ? (double)current.BytesBaixados / current.TotalBytes.Value : null));
                if (current.Status == DownloadStatus.Concluido) break;
                if (current.Status is DownloadStatus.Falhou or DownloadStatus.Pausado or DownloadStatus.Obsoleto)
                    throw new IOException(current.Erro ?? "Download pausado. Confira a conexão e o espaço livre; depois tente novamente.");
                await Task.Delay(300, ct);
            }
        }
        finally { await manager.StopAsync(CancellationToken.None); }
        progress.Report(new("Organizando", "Preparando Parquet e conferindo o resultado. Aguarde; o tempo depende do arquivo."));
        var engine = new PublicationEngine(new ServerOptions { CatalogPath = catalogPath, ConjuntosPath = definitions,
            DownloadRoot = downloadsRoot, StorageRoot = storage, DuckDbMemoryMb = 512, DuckDbThreads = 1,
            AutomaticPublicationConjuntos = ["*"] }, includeApiCompatibilityArtifacts: false);
        await engine.InitializeAsync(ct);
        await engine.ReconcileAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (engine.LastError is { Length: > 0 } error) throw new InvalidDataException(error);
        var release = (await engine.SnapshotAsync(ct)).Releases.OrderByDescending(x => x.PublishedAt).FirstOrDefault()
            ?? throw new InvalidDataException("Nenhum resultado foi gerado. Confira o cabeçalho e o formato da fonte.");
        var files = release.Partitions.Select(x => Path.Combine(storage,"releases",release.Id,x.ArquivoId,x.ParquetFile)).ToArray();
        var rows = release.Partitions.Sum(x => x.Rows);
        if (rows <= 0 || files.Any(f => !File.Exists(f))) throw new InvalidDataException("O resultado está vazio ou incompleto.");
        var manifest = Path.Combine(storage, "organization-run.json");
        await JsonStorage.SaveAsync(manifest, new { schemaVersion = 1, status = "READY", completedAt = DateTimeOffset.UtcNow,
            publicationIds = new[] { release.Id }, publications = new[] { new { release.Id, release.ConjuntoId, release.VersaoDefinicao,
                partitions = release.Partitions.Select(x => new { id = x.ArquivoId, rows = x.Rows, x.OriginalSha256,
                    parquet = $"releases/{release.Id}/{x.ArquivoId}/{x.ParquetFile}" }) } } }, ct);
        using var duck = DuckSql.Open(memoryMegabytes: 512);
        using var command = duck.CreateCommand();
        command.CommandText = $"SELECT * FROM read_parquet({DuckSql.Lit(DuckPath(files[0]))}) LIMIT 5";
        using var data = command.ExecuteReader();
        var preview = new List<string>();
        while (data.Read()) preview.Add(string.Join("  |  ", Enumerable.Range(0, Math.Min(5,data.FieldCount)).Select(i => $"{data.GetName(i)}: {Convert.ToString(data.GetValue(i))}")));
        var result = new FlowResult(storage, rows, files, string.Join(Environment.NewLine,preview), manifest);
        await JsonStorage.SaveAsync(Path.Combine(home, "resultado-desktop.json"), result, ct);
        progress.Report(new("Concluído", $"{rows:N0} linhas organizadas em {files.Length} arquivo(s) Parquet.",1));
        return result;
    }
}

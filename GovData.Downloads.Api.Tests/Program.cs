using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GovData.Downloads.Api;
using GovData.Downloads.Core;
using GovData.Downloads.Server;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
async Task Reject(Func<Task> work)
{
    try { await work(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
    { checks++; return; }
    throw new Exception("Entrada inválida foi aceita.");
}
var root = Path.GetFullPath(Path.Combine(".local", "api-tests", Guid.NewGuid().ToString("N")));
var definitions = Path.Combine(root, "conjuntos");
var run = Path.Combine(root, "run");
Directory.CreateDirectory(definitions);
var csv = new ArquivoCatalogo { Id = "arquivo-a", Nome = "Arquivo A", NomeArquivo = "a.csv", Url = "https://example.org/a.csv" };
var function = new Funcao { Id = "funcao", Nome = "Função", Arquivos = [csv] };
var agency = new Orgao { Id = "orgao", Nome = "Órgão", Funcoes = [function] };
var catalog = Path.Combine(root, "catalogo.json");
await new CatalogStore(catalog).SaveAsync(new Catalogo { Orgaos = [agency] });
var downloadRoot = Path.Combine(root, "downloads");
var source = DownloadPaths.ForFile(downloadRoot, agency, function, csv);
Directory.CreateDirectory(Path.GetDirectoryName(source)!);
var rows = new StringBuilder("codigo,nome\n");
const int total = 10000;
for (var i = 1; i <= total; i++) rows.Append(i).Append(',').Append(i % 10 == 0 ? "" : "Sorriso — ação " + new string('x', 100)).Append('\n');
await File.WriteAllTextAsync(source, rows.ToString());
var info = new FileInfo(source);
await JsonStorage.SaveAsync(Path.Combine(root, "downloads-receipts.json"), new[]
{
    new DownloadReceipt(csv.Id, csv.Url, source, null, false, null, null, DateTimeOffset.UtcNow,
        info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), null)
});
var definition = new ConjuntoDefinition
{
    Conjunto = "teste.municipios", Nome = "Teste", VersaoDefinicao = 1, Tipo = "ORGANIZADO", EdicaoPor = "ARQUIVO",
    Fontes = new() { Funcao = function.Id, Arquivos = [csv.Id], Delimiter = "," }, Cabecalho = new() { ContemUmDe = ["codigo"] },
    Colunas = new() { ["codigo"] = ["codigo"], ["nome"] = ["nome"] }, FiltroLinhas = "codigo IS NOT NULL",
    Saida = ["codigo", "nome"], Chave = ["codigo"], Validacoes = new() { ChaveUnica = true, LinhasMinPorAba = 1, Obrigatorias = ["codigo"] }
};
var definitionJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(definition, JsonStorage.Options))!;
definitionJson["cabecalho"]!.AsObject().Remove("semCabecalho");
await File.WriteAllTextAsync(Path.Combine(definitions, "teste.json"), definitionJson.ToJsonString());
var engine = new PublicationEngine(new ServerOptions
{
    CatalogPath = catalog, ConjuntosPath = definitions, DownloadRoot = downloadRoot, StorageRoot = run,
    DuckDbMemoryMb = 512, AutomaticPublicationConjuntos = ["*"]
}, includeApiCompatibilityArtifacts: false);
await engine.InitializeAsync(default);
await engine.ReconcileAsync(default);
if (engine.LastError is not null) throw new Exception(engine.LastError);
var release = (await engine.SnapshotAsync(default)).Releases.Single();
var part = release.Partitions.Single();
Check(part.Rows == total && part.Segments.Count == 0, "Fixture deve ser Parquet puro do motor atual.");
using (PublicationStateOwner.Acquire(run)) { }
async Task SaveRun(string status = "READY", long? rowCount = null, string? relative = null)
{
    await JsonStorage.SaveAsync(Path.Combine(run, "organization-run.json"), new
    {
        schemaVersion = 1, status, publicationIds = new[] { release.Id },
        publications = new[] { new { id = release.Id, release.ConjuntoId, release.VersaoDefinicao,
            partitions = new[] { new { id = part.ArquivoId, rows = rowCount ?? part.Rows, part.OriginalSha256,
                parquet = relative ?? $"releases/{release.Id}/{part.ArquivoId}/{part.ParquetFile}" } } } }
    });
}
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var options = new ApiOptions { CatalogPath = catalog, DefinitionsPath = definitions,
    RunDirectories = [run], SecretsPath = Path.Combine(root, "api-secrets.json"), Port = port, MaxPageBytes = 65536 };
await SaveRun("RUNNING");
await Reject(async () => { using var invalid = await ReadyPublicationStore.OpenAsync(options); });
await SaveRun(rowCount: total + 1);
await Reject(async () => { using var invalid = await ReadyPublicationStore.OpenAsync(options); });
await SaveRun(relative: "../outside.parquet");
await Reject(async () => { using var invalid = await ReadyPublicationStore.OpenAsync(options); });
await SaveRun();
var secrets = ApiSecrets.LoadOrCreate(options.SecretsPath);
Check(secrets == ApiSecrets.LoadOrCreate(options.SecretsPath), "Reinício alterou segredos.");
using var workerOwnership = DownloadStateOwner.Acquire(root);
using var store = await ReadyPublicationStore.OpenAsync(options);
await Reject(() => { using var write = PublicationStateOwner.Acquire(run); return Task.CompletedTask; });
var codec = new CursorCodec(Convert.FromBase64String(secrets.CursorKey));
using (var duck = DuckSql.Open(memoryMegabytes: 512))
{
    using var explain = duck.CreateCommand();
    explain.Cancel(); // O provedor precisa suportar cancelamento usado pelo timeout da API.
    checks++;
    explain.CommandText = $"EXPLAIN ANALYZE SELECT codigo FROM read_parquet({DuckSql.Lit(store.ParquetPath(release, part))}, file_row_number=true) " +
        "WHERE file_row_number >= 9990 AND file_row_number < 10000 ORDER BY file_row_number";
    using var profile = explain.ExecuteReader();
    var plan = new StringBuilder();
    while (profile.Read()) plan.AppendLine(profile.GetString(1));
    await File.WriteAllTextAsync(Path.Combine(root, "plano-pagina-final.txt"), plan.ToString());
}
var key = codec.Encode(release.ConjuntoId, part.ArquivoId, release.Id, 5, 5000);
Check(codec.Decode(key)?.Offset == 5 && codec.Decode(key + "a") is null, "Cursor não autenticado corretamente.");
await using var app = ApiHost.Build(options, secrets, store);
await app.StartAsync();
try
{
    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
    async Task<JsonDocument> GetJson(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
    }
    using (var health = await GetJson("/api/v1/saude")) Check(health.RootElement.GetProperty("status").GetString() == "ok", "Saúde inválida.");
    using (var publications = await GetJson("/api/v1/publicacoes"))
        Check(publications.RootElement.GetProperty("publicacoes").GetArrayLength() == 1, "Publicações divergentes.");
    using (var sources = await GetJson("/api/v1/fontes"))
        Check(sources.RootElement.GetProperty("fontes")[0].GetProperty("status").GetString() == "PUBLICADO", "Fonte publicada não identificada.");
    using (var partitions = await GetJson($"/api/v1/conjuntos/{release.ConjuntoId}/particoes"))
        Check(partitions.RootElement.GetProperty("particoes")[0].GetProperty("linhas").GetInt64() == total, "Contagem de partições inválida.");
    var url = $"/api/v1/conjuntos/{release.ConjuntoId}/registros?publicacao={release.Id}&particao={part.ArquivoId}&limit=5000";
    string? cursor = null;
    var seen = new HashSet<long>();
    var pages = 0;
    do
    {
        using var response = await client.GetAsync(url + (cursor is null ? "" : "&cursor=" + cursor));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync();
        Check(body.Length <= options.MaxPageBytes, "Limite em bytes excedido.");
        using var page = JsonDocument.Parse(body);
        foreach (var item in page.RootElement.GetProperty("registros").EnumerateArray())
        {
            var line = long.Parse(item.GetProperty("_linha").GetString()!);
            if (!seen.Add(line) || line != seen.Count || item.GetProperty("codigo").GetString() != line.ToString())
                throw new Exception("Paginação perdeu, duplicou ou reordenou linhas.");
            if (line % 10 == 0 && item.GetProperty("nome").ValueKind != JsonValueKind.Null)
                throw new Exception("NULL não foi preservado.");
            if (line % 10 != 0 && !item.GetProperty("nome").GetString()!.StartsWith("Sorriso — ação"))
                throw new Exception("Unicode não foi preservado.");
        }
        cursor = page.RootElement.GetProperty("proximoCursor").GetString();
        pages++;
    } while (cursor is not null && pages < 100);
    Check(seen.Count == total && cursor is null && pages > 2, "Paginação incompleta ou sem redução por bytes.");
    using (var bad = await client.GetAsync(url + "&cursor=" + key[..^1] + "!")) Check(bad.StatusCode == HttpStatusCode.BadRequest, "Cursor adulterado aceito.");
    using (var bad = await client.GetAsync(url.Replace("5000", "4000") + "&cursor=" + key)) Check(bad.StatusCode == HttpStatusCode.BadRequest, "Cursor com limite incompatível aceito.");
    using (var bad = await client.GetAsync(url + "&limit=2")) Check(bad.StatusCode == HttpStatusCode.BadRequest, "Parâmetro repetido aceito.");
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/saude"))
    {
        request.Headers.Host = "example.org:" + port;
        using var bad = await client.SendAsync(request);
        Check(bad.StatusCode == HttpStatusCode.Forbidden, "Host externo aceito.");
    }
    using (var unauthorized = await client.GetAsync("/api/v1/admin/atividade")) Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Admin sem token aceito.");
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/atividade"))
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.AdminToken);
        using var authorized = await client.SendAsync(request);
        Check(authorized.StatusCode == HttpStatusCode.OK, "Admin autenticado recusado.");
    }
    using (var request = new HttpRequestMessage(HttpMethod.Get,
        $"/api/v1/conjuntos/{release.ConjuntoId}/parquet?publicacao={release.Id}&particao={part.ArquivoId}"))
    {
        request.Headers.Range = new RangeHeaderValue(0, 3);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.PartialContent && Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()) == "PAR1", "Range Parquet inválido.");
    }
    using (var removed = await client.PostAsync("/api/v1/admin/expurgar-vencidos", null))
        Check(removed.StatusCode != HttpStatusCode.OK, "API de leitura expôs expurgo.");
    Check(ServerAccessPolicy.Classify((IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.11")), port,
        IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.12"), "192.168.1.10", port, "GET", "/api/v1/saude") == ServerAccessScope.Denied,
        "Cliente LAN não autorizado aceito.");
    Console.WriteLine($"API: {checks} verificações passaram; {total} linhas em {pages} páginas, Parquet sem segmentos JSON, Worker com estado bloqueado simultaneamente.");
    Console.WriteLine("Fixture local: " + root);
}
finally { await app.StopAsync(); }

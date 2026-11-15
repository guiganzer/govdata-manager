using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GovData.Downloads.Server;

namespace GovData.Downloads.Api;

public static class ApiHost
{
    public static WebApplication Build(ApiOptions options, ApiSecrets secrets, ReadyPublicationStore store)
    {
        options.Validate();
        var lan = options.LanEndpoint();
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls(lan is null ? [$"http://127.0.0.1:{options.Port}"] :
            [$"http://127.0.0.1:{options.Port}", $"http://{lan.Value.Address}:{options.Port}"]);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 0);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var app = builder.Build();
        var cursors = new CursorCodec(Convert.FromBase64String(secrets.CursorKey));
        var readers = new SemaphoreSlim(options.MaxReaders, options.MaxReaders);
        app.Lifetime.ApplicationStopped.Register(readers.Dispose);
        var activity = new ApiActivityMonitor();
        var releases = store.Publications.Select(x => x.Release).OrderBy(x => x.PublishedAt).ToArray();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var scope = ServerAccessPolicy.Classify(lan, options.Port, ctx.Connection.LocalIpAddress,
                ctx.Connection.RemoteIpAddress, ctx.Request.Host.Host, ctx.Request.Host.Port,
                ctx.Request.Method, ctx.Request.Path);
            if (scope == ServerAccessScope.Denied) { ctx.Response.StatusCode = 403; return; }
            if (ServerAccessPolicy.IsAdminPath(ctx.Request.Path) && !Authorized(ctx, secrets.AdminToken))
            { ctx.Response.StatusCode = 401; ctx.Response.Headers.WWWAuthenticate = "Bearer"; return; }
            var ticket = activity.Begin(ctx.Request.Method, ctx.Request.Path, ctx.Connection.RemoteIpAddress, 0);
            var originalBody = ctx.Response.Body;
            var counter = ticket.Tracked && !ctx.Request.Path.Value!.EndsWith("/parquet", StringComparison.OrdinalIgnoreCase)
                ? new CountingResponseStream(originalBody) : null;
            if (counter is not null) ctx.Response.Body = counter;
            try { await next(ctx); }
            catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
            { await Error(400, "PARAMETRO_INVALIDO", ex.Message).ExecuteAsync(ctx); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested && !ctx.Response.HasStarted)
            { await Error(503, "TEMPO_LIMITE", "Consulta excedeu o tempo configurado.").ExecuteAsync(ctx); }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested && !ctx.Response.HasStarted)
            {
                app.Logger.LogError(ex, "Falha na API: {RequestId}", ctx.TraceIdentifier);
                await Error(503, "PUBLICACAO_INDISPONIVEL", "Não foi possível ler a publicação.").ExecuteAsync(ctx);
            }
            finally
            {
                activity.Complete(ticket, ctx.Response.StatusCode, counter?.Written ?? ctx.Response.ContentLength ?? 0);
                ctx.Response.Body = originalBody;
            }
        });

        app.MapGet("/api/v1/saude", () => Results.Json(new { status = "ok", contratoVersao = 2, publicacaoErro = (string?)null }));
        app.MapGet("/api/v1/publicacoes", () => Results.Json(new
        {
            publicacoes = releases.Select(r => new
            {
                id = r.Id, conjunto = r.ConjuntoId, definicao = r.DefinitionId,
                versaoDefinicao = r.VersaoDefinicao, basePublicacao = r.BasePublicationId,
                status = r.BronzeProof is not null ? "CONFIRMADA" : r.BronzeConfirmedAt is not null ? "REVALIDACAO_BRONZE" : "DISPONIVEL",
                publicadaEm = r.PublishedAt, confirmadaEm = r.BronzeConfirmedAt,
                bronze = r.BronzeProof, expurgadaEm = r.PurgedAt,
                particoes = r.Partitions.Select(p => new { id = p.ArquivoId, linhas = p.Rows, sha256 = p.OriginalSha256 })
            })
        }));
        app.MapGet("/api/v1/fontes", (HttpContext ctx) =>
        {
            Query(ctx, "offset", "limit", "status");
            var offset = Number(ctx, "offset", 0, 0, int.MaxValue);
            var limit = Number(ctx, "limit", 100, 1, 500);
            var sources = store.Catalog.Orgaos.SelectMany(o => o.Funcoes.SelectMany(f => f.Arquivos.Select(a =>
            {
                var configured = store.Definitions.Any(d => d.Fontes?.Funcao == f.Id && ConjuntoContract.Matches(a.Id, d.Fontes.Arquivos));
                return new
                {
                    arquivoId = a.Id, nome = a.Nome, orgao = o.Id, funcao = f.Id, competencia = a.Competencia,
                    substituivel = a.Substituivel, regraAtiva = configured,
                    status = releases.Any(r => r.Partitions.Any(p => p.ArquivoId == a.Id)) ? "PUBLICADO" :
                        configured ? "AGUARDANDO_PUBLICACAO" : "SEM_LAYOUT"
                };
            }))).OrderBy(x => x.arquivoId).ToArray();
            var status = ctx.Request.Query["status"].ToString();
            if (status.Length > 0) sources = sources.Where(x => x.status == status).ToArray();
            var page = sources.Skip(offset).Take(limit).ToArray();
            return Results.Json(new { offset, limit, quantidade = page.Length, total = sources.Length,
                temMais = (long)offset + page.Length < sources.Length, fontes = page });
        });
        app.MapGet("/api/v1/documentos", () => Results.Json(new
        {
            documentos = store.Catalog.Orgaos.SelectMany(o => o.Funcoes.SelectMany(f => f.Arquivos
                .Where(a => a.NomeArquivo.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .Select(a => new { arquivoId = a.Id, nomeArquivo = a.NomeArquivo, orgao = o.Id, funcao = f.Id })))
        }));
        app.MapGet("/api/v1/conjuntos", (HttpContext ctx) =>
        {
            Query(ctx, "offset", "limit");
            var offset = Number(ctx, "offset", 0, 0, int.MaxValue);
            var limit = Number(ctx, "limit", 100, 1, 500);
            var ids = store.Definitions.Select(x => x.Conjunto).Concat(releases.Select(x => x.ConjuntoId)).Distinct().Order().ToArray();
            var page = ids.Skip(offset).Take(limit).Select(id =>
            {
                var available = releases.Where(r => r.ConjuntoId == id).ToArray();
                var count = available.SelectMany(r => r.Partitions).Select(p => p.ArquivoId).Distinct().Count();
                return new { id, nome = store.Definitions.FirstOrDefault(d => d.Conjunto == id)?.Nome ?? id,
                    particoes = new { total = count, disponiveis = count }, publicacoes = available.Select(r => r.Id).ToArray() };
            }).ToArray();
            return Results.Json(new { contratoVersao = 2, offset, limit, quantidade = page.Length,
                total = ids.Length, temMais = (long)offset + page.Length < ids.Length, conjuntos = page });
        });
        app.MapGet("/api/v1/conjuntos/{conjuntoId}/particoes", (string conjuntoId, HttpContext ctx) =>
        {
            Query(ctx, "offset", "limit", "publicacao");
            var offset = Number(ctx, "offset", 0, 0, int.MaxValue);
            var limit = Number(ctx, "limit", 500, 1, 1000);
            var id = ctx.Request.Query["publicacao"].ToString();
            var selected = releases.Where(r => r.ConjuntoId == conjuntoId && (id.Length == 0 || r.Id == id)).ToArray();
            if (selected.Length == 0) return Error(404, "PUBLICACAO_INEXISTENTE", "Publicação não disponível nos lotes configurados.");
            var parts = selected.SelectMany(r => r.Partitions.Select(p => (r, p)))
                .GroupBy(x => x.p.ArquivoId).Select(g => g.OrderByDescending(x => x.r.PublishedAt).First())
                .OrderBy(x => x.p.ArquivoId).Select(x => new
                {
                    id = x.p.ArquivoId, arquivoId = x.p.ArquivoId, status = "DISPONIVEL",
                    publicacao = x.r.Id, versao = x.p.OriginalSha256, linhas = x.p.Rows,
                    colunas = x.p.Columns, versaoDefinicao = x.r.VersaoDefinicao, validacoes = x.p.Validations
                }).ToArray();
            var page = parts.Skip(offset).Take(limit).ToArray();
            return Results.Json(new { conjunto = conjuntoId, offset, limit, quantidade = page.Length,
                total = parts.Length, temMais = (long)offset + page.Length < parts.Length, particoes = page });
        });
        app.MapGet("/api/v1/conjuntos/{conjuntoId}/registros", async (string conjuntoId, HttpContext ctx) =>
        {
            Query(ctx, "particao", "limit", "cursor", "publicacao");
            var limit = Number(ctx, "limit", 1000, 1, 50000);
            var id = Required(ctx, "publicacao");
            var partId = Required(ctx, "particao");
            var release = releases.SingleOrDefault(r => r.Id == id && r.ConjuntoId == conjuntoId);
            var part = release?.Partitions.SingleOrDefault(p => p.ArquivoId == partId);
            if (release is null || part is null) return Error(404, "PUBLICACAO_INEXISTENTE", "Publicação ou partição não disponível.");
            long start = 0;
            if (ctx.Request.Query["cursor"].ToString() is { Length: > 0 } cursor)
            {
                var decoded = cursors.Decode(cursor);
                if (decoded is null || decoded.Value.Definition != conjuntoId || decoded.Value.Partition != partId ||
                    decoded.Value.Release != id || decoded.Value.Limit != limit || decoded.Value.Offset > part.Rows)
                    return Error(400, "CURSOR_INVALIDO", "Cursor inválido ou incompatível com a requisição.");
                start = decoded.Value.Offset;
            }
            if (!await readers.WaitAsync(0, ctx.RequestAborted)) return Busy(ctx);
            try
            {
                var page = await ParquetPageReader.ReadAsync(store.ParquetPath(release, part), release, part,
                    start, limit, options, cursors, ctx.RequestAborted);
                ctx.Response.Headers["X-Versao"] = part.OriginalSha256;
                Headers(ctx, release, part);
                return Results.Bytes(page.Body, "application/json; charset=utf-8");
            }
            finally { readers.Release(); }
        });
        app.MapGet("/api/v1/conjuntos/{conjuntoId}/parquet", async (string conjuntoId, HttpContext ctx) =>
        {
            Query(ctx, "particao", "publicacao");
            var id = Required(ctx, "publicacao");
            var partId = Required(ctx, "particao");
            var release = releases.SingleOrDefault(r => r.Id == id && r.ConjuntoId == conjuntoId);
            var part = release?.Partitions.SingleOrDefault(p => p.ArquivoId == partId);
            if (release is null || part is null) return Error(404, "PUBLICACAO_INEXISTENTE", "Publicação ou partição não disponível.");
            if (!await readers.WaitAsync(0, ctx.RequestAborted)) return Busy(ctx);
            try
            {
                var stream = File.OpenRead(store.ParquetPath(release, part));
                ctx.Response.OnCompleted(() => { readers.Release(); return Task.CompletedTask; });
                Headers(ctx, release, part);
                return Results.File(stream, "application/vnd.apache.parquet", part.ArquivoId + ".parquet", enableRangeProcessing: true);
            }
            catch { readers.Release(); throw; }
        });
        app.MapGet("/api/v1/admin/atividade", () => Results.Json(activity.Snapshot()));
        return app;
    }

    private static void Headers(HttpContext ctx, PublishedRelease release, PublishedPartition part)
    {
        ctx.Response.Headers["X-Publicacao"] = release.Id;
        ctx.Response.Headers["X-Total"] = part.Rows.ToString(CultureInfo.InvariantCulture);
    }
    private static bool Authorized(HttpContext ctx, string expected)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { erro = new { codigo = code, mensagem = message } }, statusCode: status);
    private static IResult Busy(HttpContext ctx)
    {
        ctx.Response.Headers.RetryAfter = "5";
        return Error(503, "INDISPONIVEL_TEMPORARIO", "Leitores ocupados.");
    }
    private static string Required(HttpContext ctx, string key) =>
        ctx.Request.Query[key].ToString() is { Length: > 0 } value ? value : throw new BadHttpRequestException("Informe " + key + ".");
    private static int Number(HttpContext ctx, string key, int fallback, int min, int max)
    {
        if (!ctx.Request.Query.ContainsKey(key)) return fallback;
        if (!int.TryParse(ctx.Request.Query[key], out var value) || value < min || value > max)
            throw new BadHttpRequestException(key + " inválido.");
        return value;
    }
    private static void Query(HttpContext ctx, params string[] keys)
    {
        if (ctx.Request.Query.Any(x => !keys.Contains(x.Key) || x.Value.Count != 1))
            throw new BadHttpRequestException("Parâmetro desconhecido ou repetido.");
    }
}

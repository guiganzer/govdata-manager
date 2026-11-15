using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GovData.Downloads.Core;

public sealed class Catalogo
{
    public int Versao { get; set; } = 2;
    public List<Orgao> Orgaos { get; set; } = [];
}

public sealed class AppSettings
{
    public string DownloadRoot { get; set; } = "";
    public int MaxConcurrentDownloads { get; set; } = 3;
    /// <summary>Downloads simultâneos permitidos por órgão do catálogo, quando o órgão não tem limite próprio.</summary>
    public int MaxDownloadsPorOrgao { get; set; } = 2;
    /// <summary>Limite próprio por órgão (id do catálogo → downloads simultâneos), de 1 a 16.</summary>
    public Dictionary<string, int> LimitesPorOrgao { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AppProfile Profile { get; set; } = new();
}

public sealed class AppProfile
{
    public bool AutomationEnabled { get; set; } = true;
}

public sealed class AppSettingsStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new AppSettings();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonStorage.Options, cancellationToken)
            ?? throw new JsonException("Configuração local vazia.");
    }

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadAsync(cancellationToken);
            settings.Profile ??= new AppProfile();
            update(settings);
            await JsonStorage.SaveAsync(path, settings, cancellationToken);
            return settings;
        }
        finally { _gate.Release(); }
    }
}

public sealed class Orgao
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nome { get; set; } = "";
    public List<Funcao> Funcoes { get; set; } = [];
}

public sealed class Funcao
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nome { get; set; } = "";
    public List<ArquivoCatalogo> Arquivos { get; set; } = [];
}

public sealed class ArquivoCatalogo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nome { get; set; } = "";
    public string Url { get; set; } = "";
    public string NomeArquivo { get; set; } = "";
    public string? Competencia { get; set; }
    public bool Substituivel { get; set; }
    public string? FrequenciaVerificacao { get; set; }
    public string? PaginaOrigem { get; set; }
    public string? Descricao { get; set; }
    public long? TamanhoEsperadoBytes { get; set; }
    public string? Sha256 { get; set; }
}

public static class JsonStorage
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), cancellationToken);
            }
        }
    }
}

public sealed class CatalogStore(string path)
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.Compiled);
    private static readonly Regex ShaPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    private static readonly Regex CompetenciaPattern = new("^[0-9]{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);
    private static readonly Regex ReservedNamePattern = new("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Path { get; } = path;
    public event Action? Saved;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(Path)) await JsonStorage.SaveAsync(Path, new Catalogo(), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<Catalogo> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return new Catalogo();
        await using var stream = File.OpenRead(Path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        CatalogContract.ValidateJson(document.RootElement);
        var catalogo = document.Deserialize<Catalogo>(JsonStorage.Options)
            ?? throw new InvalidDataException("O catálogo JSON está vazio.");
        Validate(catalogo);
        return catalogo;
    }

    public async Task SaveAsync(Catalogo catalogo, CancellationToken cancellationToken = default)
    {
        Validate(catalogo);
        await _gate.WaitAsync(cancellationToken);
        try { await JsonStorage.SaveAsync(Path, catalogo, cancellationToken); }
        finally { _gate.Release(); }
        Saved?.Invoke();
    }

    public static void Validate(Catalogo catalogo)
    {
        if (catalogo.Versao != 2) throw new InvalidDataException($"Versão de catálogo não suportada: {catalogo.Versao}. Use o contrato v2.");
        if (catalogo.Orgaos is null) throw new InvalidDataException("O catálogo precisa de uma lista de órgãos.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var orgao in catalogo.Orgaos)
        {
            Check(orgao.Id, orgao.Nome, "órgão");
            if (orgao.Funcoes is null) throw new InvalidDataException($"Funções ausentes em {orgao.Nome}.");
            foreach (var funcao in orgao.Funcoes)
            {
                Check(funcao.Id, funcao.Nome, "função");
                if (funcao.Arquivos is null) throw new InvalidDataException($"Arquivos ausentes em {funcao.Nome}.");
                var nomesArquivo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var arquivo in funcao.Arquivos)
                {
                    Check(arquivo.Id, arquivo.Nome, "arquivo");
                    if (!Uri.TryCreate(arquivo.Url, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp)
                        || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
                        || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath.EndsWith('/'))
                        throw new InvalidDataException($"URL HTTPS ou FTP inválida: {arquivo.Nome}.");
                    if (string.IsNullOrWhiteSpace(arquivo.NomeArquivo)
                        || arquivo.NomeArquivo.Length > 255
                        || arquivo.NomeArquivo is "." or ".."
                        || arquivo.NomeArquivo.EndsWith('.') || arquivo.NomeArquivo.EndsWith(' ')
                        || arquivo.NomeArquivo != System.IO.Path.GetFileName(arquivo.NomeArquivo)
                        || arquivo.NomeArquivo.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                        || ReservedNamePattern.IsMatch(arquivo.NomeArquivo.Split('.')[0]))
                        throw new InvalidDataException($"Nome de arquivo inválido: {arquivo.NomeArquivo}.");
                    if (!nomesArquivo.Add(arquivo.NomeArquivo))
                        throw new InvalidDataException($"nomeArquivo duplicado na função {funcao.Nome}: {arquivo.NomeArquivo}.");
                    if (arquivo.Competencia is not null && !CompetenciaPattern.IsMatch(arquivo.Competencia))
                        throw new InvalidDataException($"Competência inválida em {arquivo.Nome}: use AAAA-MM.");
                    if (arquivo.PaginaOrigem is not null
                        && (!Uri.TryCreate(arquivo.PaginaOrigem, UriKind.Absolute, out var pagina)
                            || (pagina.Scheme != Uri.UriSchemeHttps && pagina.Scheme != Uri.UriSchemeHttp)
                            || string.IsNullOrEmpty(pagina.Host) || !string.IsNullOrEmpty(pagina.UserInfo)))
                        throw new InvalidDataException($"Página de origem HTTP(S) inválida: {arquivo.Nome}.");
                    if (arquivo.Descricao?.Length > 1000)
                        throw new InvalidDataException($"Descrição longa demais: {arquivo.Nome}.");
                    if (arquivo.TamanhoEsperadoBytes < 0)
                        throw new InvalidDataException($"Tamanho esperado inválido: {arquivo.Nome}.");
                    if (arquivo.Sha256 is not null && !ShaPattern.IsMatch(arquivo.Sha256))
                        throw new InvalidDataException($"SHA-256 inválido: {arquivo.Nome}.");
                    if (arquivo.Substituivel && (arquivo.TamanhoEsperadoBytes is not null || arquivo.Sha256 is not null))
                        throw new InvalidDataException($"Fonte substituível não pode ter tamanho esperado ou SHA-256 fixo: {arquivo.Nome}.");
                    if (arquivo.FrequenciaVerificacao is not null
                        && (!arquivo.Substituivel || arquivo.FrequenciaVerificacao is not ("diaria" or "mensal" or "anual")))
                        throw new InvalidDataException($"Frequência de verificação inválida em {arquivo.Nome}: use diaria, mensal ou anual apenas para fonte substituível.");
                    var target = DownloadPaths.ForFile(System.IO.Path.GetTempPath(), orgao, funcao, arquivo);
                    if (!targets.Add(target))
                        throw new InvalidDataException($"Dois arquivos apontam para o mesmo destino: {target}.");
                }
            }
        }

        void Check(string id, string nome, string kind)
        {
            if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id) || !ids.Add(id))
                throw new InvalidDataException($"ID de {kind} inválido ou duplicado: {id}.");
            if (string.IsNullOrWhiteSpace(nome) || nome.Length > 160)
                throw new InvalidDataException($"Nome de {kind} vazio ou longo demais: {id}.");
        }
    }
}

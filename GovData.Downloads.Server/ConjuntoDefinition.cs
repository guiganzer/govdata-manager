using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GovData.Downloads.Core;

namespace GovData.Downloads.Server;

public sealed class ConjuntoDefinition
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public string Conjunto { get; set; } = "";
    public string Nome { get; set; } = "";
    public int VersaoDefinicao { get; set; }
    public string Tipo { get; set; } = "";
    public string EdicaoPor { get; set; } = "";
    public ConjuntoSources? Fontes { get; set; }
    public HeaderRule? Cabecalho { get; set; }
    public Dictionary<string, string[]> Colunas { get; set; } = [];
    public Dictionary<string, string> Derivadas { get; set; } = [];
    public string? FiltroLinhas { get; set; }
    public string[] Saida { get; set; } = [];
    public string[] Chave { get; set; } = [];
    public MunicipalityCode? MunicipioCodigo { get; set; }
    public Dictionary<string, string> Referencias { get; set; } = [];
    public QualityRules Validacoes { get; set; } = new();
    public string? Base { get; set; }
    public string? Consulta { get; set; }
}
public sealed class ConjuntoSources
{
    public string Funcao { get; set; } = "";
    public string[] Arquivos { get; set; } = [];
    public string[]? Membros { get; set; }
    public string? FormatoMembro { get; set; }
    public string[]? Abas { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public string Delimiter { get; set; } = ",";
}
public sealed class HeaderRule
{
    public string[]? ContemUmDe { get; set; }
    public int? Linha { get; set; }
    public bool SemCabecalho { get; set; }
}
public sealed class MunicipalityCode { public string Coluna { get; set; } = ""; public string Tipo { get; set; } = ""; }
public sealed class QualityRules
{
    public long LinhasMinPorAba { get; set; }
    public bool ChaveUnica { get; set; }
    public string[] ColunasPresentes { get; set; } = [];
    public string[] Obrigatorias { get; set; } = [];
    public Dictionary<string, double> NulosMaxPct { get; set; } = [];
    public double SemCorrespondenciaMaxPct { get; set; }
}
public sealed class QualityReport
{
    public List<string> Avisos { get; set; } = [];
    public List<string> Erros { get; set; } = [];
    public List<SheetCount> Abas { get; set; } = [];
}
public sealed record SheetCount(string Arquivo, string? Membro, string? Aba, long Linhas);

public static class ConjuntoContract
{
    public static readonly JsonSerializerOptions Json = new(JsonStorage.Options)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static byte[] CanonicalJson(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonStorage.Options);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) WriteSorted(element, writer);
        return output.ToArray();
    }
    private static void WriteSorted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteSorted(property.Value, writer); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteSorted(item, writer);
                writer.WriteEndArray();
                break;
            default: element.WriteTo(writer); break;
        }
    }
    public static bool Matches(string value, IEnumerable<string>? patterns) => patterns is null ||
        patterns.Any(p => Regex.IsMatch(value, "^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    public static ConjuntoDefinition Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        CheckDuplicates(doc.RootElement);
        using var schemaStream = typeof(ConjuntoContract).Assembly.GetManifestResourceStream("conjunto-v3.schema.json")!;
        using var schemaReader = new StreamReader(schemaStream);
        var schema = global::Json.Schema.JsonSchema.FromText(schemaReader.ReadToEnd());
        if (!schema.Evaluate(doc.RootElement).IsValid) throw new InvalidDataException("Definição não atende ao JSON Schema V3: " + path);
        var d = doc.RootElement.Deserialize<ConjuntoDefinition>(Json) ?? throw new InvalidDataException("Definição vazia.");
        Validate(d);
        return d;
    }
    private static void CheckDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Campo duplicado: " + p.Name); CheckDuplicates(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var v in e.EnumerateArray()) CheckDuplicates(v);
    }
    public static void Validate(ConjuntoDefinition d)
    {
        bool Name(string s) => Regex.IsMatch(s, "^[a-z][a-z0-9_]*$");
        if (!Regex.IsMatch(d.Conjunto, "^[a-z0-9][a-z0-9._-]{0,79}$") || string.IsNullOrWhiteSpace(d.Nome) ||
            d.VersaoDefinicao < 1 || d.Tipo is not ("ORGANIZADO" or "DERIVADO") || d.EdicaoPor is not ("ARQUIVO" or "LISTA"))
            throw new InvalidDataException("Identidade, tipo ou edição de conjunto inválidos.");
        if (d.Saida.Length == 0 || d.Saida.Any(s => !Name(s)) || d.Saida.Distinct().Count() != d.Saida.Length ||
            d.Chave.Any(s => !d.Saida.Contains(s)) || d.Validacoes.Obrigatorias.Any(s => !d.Saida.Contains(s)) ||
            d.Validacoes.ColunasPresentes.Any(s => !d.Colunas.ContainsKey(s)) ||
            d.Validacoes.NulosMaxPct.Any(p => !d.Saida.Contains(p.Key) || p.Value is < 0 or > 100) ||
            d.Validacoes.LinhasMinPorAba < 0 || d.Validacoes.SemCorrespondenciaMaxPct is < 0 or > 100 ||
            d.Validacoes.ChaveUnica && d.Chave.Length == 0)
            throw new InvalidDataException("Saída, chave ou validações inválidas.");
        if (d.Tipo == "ORGANIZADO")
        {
            if (d.Fontes is null || d.Fontes.Arquivos.Length == 0 || string.IsNullOrWhiteSpace(d.Fontes.Funcao) ||
                d.Fontes.FormatoMembro is not (null or "csv" or "txt" or "tsv" or "xlsx" or "xls" or "dbf" or "dbc") ||
                d.Cabecalho is null ||
                ((d.Cabecalho.Linha.HasValue ? 1 : 0) + (d.Cabecalho.ContemUmDe is { Length: > 0 } ? 1 : 0) + (d.Cabecalho.SemCabecalho ? 1 : 0) != 1) ||
                d.Cabecalho.Linha is < 1 || d.Colunas.Count == 0 || string.IsNullOrWhiteSpace(d.FiltroLinhas) ||
                d.Colunas.Any(p => !Name(p.Key) || p.Value.Length == 0) || d.Derivadas.Keys.Any(k => !Name(k)) ||
                d.Saida.Any(s => !d.Colunas.ContainsKey(s) && !d.Derivadas.ContainsKey(s)))
                throw new InvalidDataException("Fonte, cabeçalho ou mapeamento inválido.");
            CheckSql(d.FiltroLinhas!, false, d.Referencias.Keys);
            foreach (var expr in d.Derivadas.Values) CheckSql(expr, false, d.Referencias.Keys);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(d.Base) || string.IsNullOrWhiteSpace(d.Consulta)) throw new InvalidDataException("Derivado exige base e consulta.");
            CheckSql(d.Consulta!, true, d.Referencias.Keys);
        }
        foreach (var p in d.Referencias)
            if (!Name(p.Key) || !p.Value.Replace('\\','/').StartsWith("server/referencias/", StringComparison.Ordinal) ||
                p.Value.Contains("..") || Path.IsPathRooted(p.Value) || !p.Value.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Referência deve ser CSV dentro de server/referencias.");
        if (d.MunicipioCodigo is { } m && (!d.Saida.Contains(m.Coluna) || m.Tipo is not ("IBGE7" or "IBGE6" or "TSE" or "RFB" or "NOME_UF")))
            throw new InvalidDataException("Identificação municipal inválida.");
    }
    public static void CheckSql(string sql, bool query, IEnumerable<string> references)
    {
        // Strip string literals before checking SQL tokens; reject comments and multiple statements.
        var clean = Regex.Replace(sql, "'(?:''|[^'])*'", "''");
        if (clean.Contains(';') || clean.Contains("--") || clean.Contains("/*") || clean.Contains('$') || clean.Contains('"') ||
            Regex.IsMatch(clean, @"\b(attach|detach|copy|install|load|pragma|set|insert|update|delete|drop|create|alter|call|export|import|read_csv|read_parquet|read_json|read_text|glob|query|query_table)\b", RegexOptions.IgnoreCase) ||
            (!query && Regex.IsMatch(clean, @"\b(select|from|with|union)\b", RegexOptions.IgnoreCase)) ||
            query && (!Regex.IsMatch(clean.TrimStart(), @"^select\b", RegexOptions.IgnoreCase) ||
                      Regex.Matches(clean, @"\bselect\b", RegexOptions.IgnoreCase).Count != 1 ||
                      !Regex.IsMatch(clean, @"\bfrom\s+base\b", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("SQL fora do contrato de expressões.");
        var functions = new HashSet<string>(("coalesce nullif regexp_extract regexp_full_match regexp_replace lower upper trim ltrim rtrim " +
            "concat concat_ws substr substring replace length left right cast try_cast count sum min max avg round abs floor ceil greatest least in filter over as").Split(' '), StringComparer.OrdinalIgnoreCase);
        functions.UnionWith(references);
        foreach (Match match in Regex.Matches(clean, @"\b([a-zA-Z_][a-zA-Z0-9_]*)\s*\("))
            if (!functions.Contains(match.Groups[1].Value)) throw new InvalidDataException("Função SQL não autorizada: " + match.Groups[1].Value);
    }
}

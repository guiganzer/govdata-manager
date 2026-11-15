using System.Text.Json;

namespace GovData.Downloads.Core;

public static class CatalogContract
{
    public static void ValidateJson(JsonElement root)
    {
        Object(root, "$", ["versao", "orgaos"]);
        Number(root.GetProperty("versao"), "$.versao");
        if (root.GetProperty("versao").GetInt64() != 2)
            throw Error("$.versao", "contrato v2 exige o valor 2");
        var orgaos = Array(root.GetProperty("orgaos"), "$.orgaos");
        var oi = 0;
        foreach (var orgao in orgaos.EnumerateArray())
        {
            var op = $"$.orgaos[{oi++}]";
            Object(orgao, op, ["id", "nome", "funcoes"]);
            String(orgao.GetProperty("id"), op + ".id");
            String(orgao.GetProperty("nome"), op + ".nome");
            var funcoes = Array(orgao.GetProperty("funcoes"), op + ".funcoes");
            var fi = 0;
            foreach (var funcao in funcoes.EnumerateArray())
            {
                var fp = $"{op}.funcoes[{fi++}]";
                Object(funcao, fp, ["id", "nome", "arquivos"]);
                String(funcao.GetProperty("id"), fp + ".id");
                String(funcao.GetProperty("nome"), fp + ".nome");
                var arquivos = Array(funcao.GetProperty("arquivos"), fp + ".arquivos");
                var ai = 0;
                foreach (var arquivo in arquivos.EnumerateArray())
                {
                    var ap = $"{fp}.arquivos[{ai++}]";
                    Object(arquivo, ap, ["id", "nome", "url", "nomeArquivo", "substituivel"],
                        ["competencia", "frequenciaVerificacao", "paginaOrigem", "descricao", "tamanhoEsperadoBytes", "sha256"]);
                    foreach (var field in new[] { "id", "nome", "url", "nomeArquivo" })
                        String(arquivo.GetProperty(field), ap + "." + field);
                    if (arquivo.TryGetProperty("descricao", out var descricao)) String(descricao, ap + ".descricao");
                    if (arquivo.TryGetProperty("competencia", out var competencia)) String(competencia, ap + ".competencia");
                    if (arquivo.TryGetProperty("frequenciaVerificacao", out var frequencia)) String(frequencia, ap + ".frequenciaVerificacao");
                    if (arquivo.TryGetProperty("paginaOrigem", out var paginaOrigem)) String(paginaOrigem, ap + ".paginaOrigem");
                    Boolean(arquivo.GetProperty("substituivel"), ap + ".substituivel");
                    if (arquivo.TryGetProperty("tamanhoEsperadoBytes", out var size)) Number(size, ap + ".tamanhoEsperadoBytes");
                    if (arquivo.TryGetProperty("sha256", out var sha)) String(sha, ap + ".sha256");
                }
            }
        }
    }

    private static void Object(JsonElement value, string path, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error(path, "deve ser um objeto");
        foreach (var name in required)
            if (!value.TryGetProperty(name, out _)) throw Error(path + "." + name, "campo obrigatório ausente");
        var allowed = required.Concat(optional ?? []).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Error(path + "." + property.Name, "campo duplicado");
            if (!allowed.Contains(property.Name)) throw Error(path + "." + property.Name, "campo não reconhecido pelo contrato v2");
        }
    }

    private static JsonElement Array(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array) throw Error(path, "deve ser uma lista");
        return value;
    }

    private static void String(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String) throw Error(path, "deve ser texto");
    }

    private static void Boolean(JsonElement value, string path)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error(path, "deve ser verdadeiro ou falso");
    }

    private static void Number(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
            throw Error(path, "deve ser um número inteiro");
    }

    private static InvalidDataException Error(string path, string message) => new($"{path}: {message}.");
}

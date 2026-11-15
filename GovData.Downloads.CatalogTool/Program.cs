using System.Text.Json;
using GovData.Downloads.Core;

if (args is ["validate-conjuntos", var input])
{
    try
    {
        var paths=Directory.Exists(input) ? Directory.EnumerateFiles(input,"*.json").ToArray() : [input];
        if (paths.Length==0) throw new InvalidDataException("Pasta sem conjuntos.");
        foreach(var file in paths) { var d=GovData.Downloads.Server.ConjuntoContract.Read(file); Console.WriteLine(d.Conjunto+": válido"); }
        return 0;
    }
    catch(Exception ex) when (ex is JsonException or InvalidDataException or IOException or ArgumentException)
    { Console.Error.WriteLine(ex.Message); return 2; }
}

var path = args.Length switch
{
    1 => Path.GetFullPath(args[0]),
    2 when args[0] == "validate" => Path.GetFullPath(args[1]),
    _ => ""
};

if (path.Length == 0)
{
    Console.Error.WriteLine("Uso: dotnet run --project GovData.Downloads.CatalogTool -- validate <catalogo.json>");
    return 64;
}

try
{
    if (!File.Exists(path)) throw new FileNotFoundException("Catálogo não encontrado.", path);
    var catalog = await new CatalogStore(path).LoadAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        contrato = "catalogo-v2",
        caminho = path,
        orgaos = catalog.Orgaos.Count,
        funcoes = catalog.Orgaos.Sum(o => o.Funcoes.Count),
        arquivos = catalog.Orgaos.Sum(o => o.Funcoes.Sum(f => f.Arquivos.Count))
    }, JsonStorage.Options));
    return 0;
}
catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        contrato = "catalogo-v2",
        caminho = path,
        tipo = ex.GetType().Name,
        erro = ex.Message
    }, JsonStorage.Options));
    return 2;
}

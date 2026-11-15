using GovData.Downloads.Api;

try
{
    if (args.Length != 3 || args[0] is not ("serve" or "init") || args[1] != "--config")
        throw new ArgumentException("Uso: GovData.Downloads.Api init|serve --config CAMINHO");
    var options = await ApiOptions.LoadAsync(args[2]);
    var secrets = ApiSecrets.LoadOrCreate(options.SecretsPath);
    if (args[0] == "init")
    {
        Console.WriteLine("Segredos locais preparados. Nenhum lote ou download foi modificado.");
        return 0;
    }
    using var store = await ReadyPublicationStore.OpenAsync(options);
    await using var app = ApiHost.Build(options, secrets, store);
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

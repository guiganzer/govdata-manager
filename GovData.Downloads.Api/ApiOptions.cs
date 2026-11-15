using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GovData.Downloads.Core;

namespace GovData.Downloads.Api;

public sealed class ApiOptions
{
    public int Port { get; set; } = 5080;
    public string CatalogPath { get; set; } = "catalogo.json";
    public string DefinitionsPath { get; set; } = "server/conjuntos";
    public string[] RunDirectories { get; set; } = [];
    public string SecretsPath { get; set; } = "api-secrets.json";
    public string? LanAddress { get; set; }
    public string? LanAllowedClient { get; set; }
    public int MaxReaders { get; set; } = 2;
    public int MaxPageBytes { get; set; } = 8 * 1024 * 1024;
    public int DuckDbMemoryMb { get; set; } = 256;
    public int QueryTimeoutSeconds { get; set; } = 60;

    public static async Task<ApiOptions> LoadAsync(string path)
    {
        path = Path.GetFullPath(path);
        var options = JsonSerializer.Deserialize<ApiOptions>(await File.ReadAllTextAsync(path), JsonStorage.Options)
            ?? throw new InvalidDataException("Configuração da API vazia.");
        var root = Path.GetDirectoryName(path)!;
        string Resolve(string value) => Path.GetFullPath(value, root);
        options.CatalogPath = Resolve(options.CatalogPath);
        options.DefinitionsPath = Resolve(options.DefinitionsPath);
        options.SecretsPath = Resolve(options.SecretsPath);
        options.RunDirectories = options.RunDirectories.Select(Resolve).ToArray();
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Port is < 1024 or > 65535 || MaxReaders is < 1 or > 32 ||
            MaxPageBytes is < 65536 or > 16 * 1024 * 1024 ||
            DuckDbMemoryMb is < 128 or > 4096 || QueryTimeoutSeconds is < 1 or > 600 ||
            RunDirectories.Length == 0 || RunDirectories.Any(string.IsNullOrWhiteSpace) ||
            RunDirectories.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != RunDirectories.Length)
            throw new InvalidDataException("Porta, leitores, memória, timeout ou diretórios da API inválidos.");
        _ = LanEndpoint();
    }

    public (IPAddress Address, IPAddress Client)? LanEndpoint()
    {
        if (LanAddress is null && LanAllowedClient is null) return null;
        if (!IPAddress.TryParse(LanAddress, out var address) || !IPAddress.TryParse(LanAllowedClient, out var client) ||
            !Private(address) || !Private(client) || address.Equals(client) ||
            address.ToString() != LanAddress || client.ToString() != LanAllowedClient)
            throw new InvalidDataException("Informe endereço LAN e cliente IPv4 privados e distintos, ou deixe ambos nulos.");
        return (address, client);
    }

    private static bool Private(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168);
    }
}

public sealed record ApiSecrets(string AdminToken, string CursorKey)
{
    public static ApiSecrets LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Restringir o arquivo vazio antes de escrever qualquer segredo.
                using (File.Create(temporary)) { }
                SecretFilePermissions.RestrictToCurrentUser(temporary);
                File.WriteAllText(temporary, JsonSerializer.Serialize(new ApiSecrets(
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), JsonStorage.Options));
                File.Move(temporary, path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        SecretFilePermissions.VerifyReadable(path);
        var secrets = JsonSerializer.Deserialize<ApiSecrets>(File.ReadAllText(path), JsonStorage.Options)
            ?? throw new InvalidDataException("Segredos da API vazios.");
        if (secrets.AdminToken.Length < 32 || Convert.FromBase64String(secrets.CursorKey).Length < 32)
            throw new InvalidDataException("Chaves da API inválidas.");
        return secrets;
    }
}

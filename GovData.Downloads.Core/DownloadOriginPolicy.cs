using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace GovData.Downloads.Core;

public sealed class DownloadOriginRule
{
    public string Scheme { get; set; } = "";
    public string Host { get; set; } = "";
    public string PathPrefix { get; set; } = "";
}

public sealed class DownloadOriginRules
{
    public List<DownloadOriginRule> AllowedOrigins { get; set; } = [];
}

public sealed class DownloadOriginPolicy
{
    private readonly DownloadOriginRule[] _rules;

    public DownloadOriginPolicy(IEnumerable<DownloadOriginRule> rules)
    {
        _rules = rules.ToArray();
        if (_rules.Length == 0 || _rules.Any(x => x.Scheme is not ("https" or "ftp") ||
            string.IsNullOrWhiteSpace(x.Host) || !x.PathPrefix.StartsWith('/') ||
            !x.PathPrefix.EndsWith('/') && !x.PathPrefix.EndsWith(".php/")))
            throw new InvalidDataException("Lista de origens autorizadas inválida.");
    }

    public static DownloadOriginPolicy Load(string path)
    {
        var data = JsonSerializer.Deserialize<DownloadOriginRules>(File.ReadAllText(path), JsonStorage.Options)
            ?? throw new InvalidDataException("Lista de origens vazia.");
        return new DownloadOriginPolicy(data.AllowedOrigins);
    }

    public void Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "ftp") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IsDefaultPort || IPAddress.TryParse(uri.Host, out _) ||
            !_rules.Any(x => x.Scheme == uri.Scheme &&
                x.Host.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith(x.PathPrefix, StringComparison.Ordinal)))
            throw new InvalidDataException("Origem de download fora da lista autorizada.");
    }

    public async Task<IPAddress> ResolvePublicAsync(Uri uri, CancellationToken ct)
    {
        Validate(uri);
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        if (addresses.Length == 0 || addresses.Any(x => !IsPublic(x)))
            throw new InvalidDataException("DNS da origem retornou endereço local ou reservado.");
        return addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
    }

    public HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(x => !IsPublic(x)))
                    throw new InvalidDataException("DNS da origem retornou endereço local ou reservado.");
                var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler);
    }

    private static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes6 = address.GetAddressBytes();
            // Only native global unicast is accepted. Tunnel and documentation ranges
            // can encode destinations that must never be reached by a downloader.
            return (bytes6[0] & 0xE0) == 0x20 &&
                !(bytes6[0] == 0x20 && bytes6[1] == 0x01 && bytes6[2] == 0x0D && bytes6[3] == 0xB8) &&
                !(bytes6[0] == 0x20 && bytes6[1] == 0x01 && bytes6[2] == 0x00 && bytes6[3] == 0x00) &&
                !(bytes6[0] == 0x20 && bytes6[1] == 0x02);
        }
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 168) &&
            !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) &&
            !(b[0] == 192 && b[1] == 88 && b[2] == 99) &&
            !(b[0] == 198 && b[1] is 18 or 19) &&
            !(b[0] == 198 && b[1] == 51 && b[2] == 100) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}

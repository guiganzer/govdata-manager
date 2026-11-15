using System.Net;

namespace GovData.Downloads.Api;

public enum ServerAccessScope { Denied, Local, LanRead }

public static class ServerAccessPolicy
{
    public static ServerAccessScope Classify(
        (IPAddress Address, IPAddress Client)? lan, int port, IPAddress? localAddress,
        IPAddress? remoteAddress, string host, int? hostPort, string method, string path)
    {
        if (hostPort != port || localAddress is null || remoteAddress is null)
            return ServerAccessScope.Denied;
        var local = localAddress.IsIPv4MappedToIPv6 ? localAddress.MapToIPv4() : localAddress;
        var remote = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        if (IPAddress.IsLoopback(local) && IPAddress.IsLoopback(remote) &&
            (host.Equals("127.0.0.1", StringComparison.Ordinal) ||
             host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            return ServerAccessScope.Local;
        if (lan is null || !local.Equals(lan.Value.Address) || !remote.Equals(lan.Value.Client) ||
            !host.Equals(lan.Value.Address.ToString(), StringComparison.Ordinal) ||
            !method.Equals("GET", StringComparison.Ordinal))
            return ServerAccessScope.Denied;
        return IsReadPath(path) ? ServerAccessScope.LanRead : ServerAccessScope.Denied;
    }

    public static bool IsAdminPath(string path) =>
        path.Equals("/api/v1/admin", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/v1/admin/", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadPath(string path)
    {
        if (path.Equals("/api/v1/saude", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/publicacoes", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/fontes", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/documentos", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v1/conjuntos", StringComparison.OrdinalIgnoreCase))
            return true;
        const string prefix = "/api/v1/conjuntos/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;
        var endpoint = rest[(slash + 1)..];
        return endpoint.Equals("parquet", StringComparison.OrdinalIgnoreCase) ||
               endpoint.Equals("particoes", StringComparison.OrdinalIgnoreCase) ||
               endpoint.Equals("registros", StringComparison.OrdinalIgnoreCase);
    }
}

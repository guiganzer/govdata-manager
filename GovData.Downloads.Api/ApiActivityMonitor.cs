using System.Diagnostics;
using System.Net;

namespace GovData.Downloads.Api;

public sealed record ApiActivityEvent(DateTimeOffset At, string Method, string Endpoint,
    string Client, int Status, long ReceivedBytes, long SentBytes, long DurationMs);

public sealed record ApiEndpointUsage(string Endpoint, long Requests, long Errors,
    long ReceivedBytes, long SentBytes);

public sealed record ApiActivitySnapshot(long Requests, int Active, long Success, long ClientErrors,
    long ServerErrors, long ReceivedBytes, long SentBytes, DateTimeOffset? LastRequestAt,
    ApiEndpointUsage[] Endpoints, ApiActivityEvent[] Recent);

public readonly record struct ApiRequestTicket(string Endpoint, string Method, string Client,
    long ReceivedBytes, long StartedAt, bool Tracked);

/// <summary>Contadores locais e um histórico limitado, sem URL de consulta nem tokens.</summary>
public sealed class ApiActivityMonitor
{
    public const string SentBytesKey = "govdata.api.sent-bytes";
    private const int RecentLimit = 12;
    private const int EndpointLimit = 128;
    private readonly object _gate = new();
    private readonly Queue<ApiActivityEvent> _recent = new();
    private readonly Dictionary<string, ApiEndpointUsage> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private long _requests, _success, _clientErrors, _serverErrors, _received, _sent;
    private int _active;
    private DateTimeOffset? _lastRequestAt;

    public ApiRequestTicket Begin(string method, string path, IPAddress? remote, long? contentLength)
    {
        if (IsPanelPoll(path)) return default;
        var endpoint = Normalize(path);
        var address = remote?.IsIPv4MappedToIPv6 == true ? remote.MapToIPv4() : remote;
        var client = address is null ? "desconhecido" : IPAddress.IsLoopback(address) ? "local" : address.ToString();
        lock (_gate) _active++;
        return new(endpoint, method.ToUpperInvariant(), client, Math.Max(0, contentLength ?? 0),
            Stopwatch.GetTimestamp(), true);
    }

    public void Complete(ApiRequestTicket ticket, int status, long sentBytes)
    {
        if (!ticket.Tracked) return;
        var at = DateTimeOffset.UtcNow;
        var elapsed = Math.Max(0, (long)Stopwatch.GetElapsedTime(ticket.StartedAt).TotalMilliseconds);
        var sent = Math.Max(0, sentBytes);
        var item = new ApiActivityEvent(at, ticket.Method, ticket.Endpoint, ticket.Client,
            status, ticket.ReceivedBytes, sent, elapsed);
        lock (_gate)
        {
            _active--;
            _requests++;
            _received += ticket.ReceivedBytes;
            _sent += sent;
            if (status >= 500) _serverErrors++;
            else if (status >= 400) _clientErrors++;
            else _success++;
            _lastRequestAt = at;
            if (_recent.Count == RecentLimit) _recent.Dequeue();
            _recent.Enqueue(item);
            var key = ticket.Endpoint;
            if (!_endpoints.ContainsKey(key) && _endpoints.Count >= EndpointLimit) key = "/outros";
            _endpoints.TryGetValue(key, out var previous);
            _endpoints[key] = new(key, (previous?.Requests ?? 0) + 1,
                (previous?.Errors ?? 0) + (status >= 400 ? 1 : 0),
                (previous?.ReceivedBytes ?? 0) + ticket.ReceivedBytes,
                (previous?.SentBytes ?? 0) + sent);
        }
    }

    public ApiActivitySnapshot Snapshot()
    {
        lock (_gate)
            return new(_requests, _active, _success, _clientErrors, _serverErrors,
                _received, _sent, _lastRequestAt,
                _endpoints.Values.OrderByDescending(x => x.Requests).ThenBy(x => x.Endpoint)
                    .Take(6).ToArray(), _recent.Reverse().ToArray());
    }

    private static bool IsPanelPoll(string path) =>
        path.Equals("/api/v1/admin/atividade", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/v1/admin/publicacao-resumo", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length == 64 && parts[i].All(char.IsAsciiHexDigit) ||
                Guid.TryParse(parts[i], out _)) parts[i] = "{id}";
        var normalized = "/" + string.Join('/', parts);
        return normalized.Length <= 180 ? normalized : normalized[..180] + "…";
    }
}

internal sealed class CountingResponseStream(Stream inner) : Stream
{
    private long _written;
    public long Written => Interlocked.Read(ref _written);
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref _written, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Interlocked.Add(ref _written, buffer.Length);
    }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer, offset, count, cancellationToken);
        Interlocked.Add(ref _written, count);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Interlocked.Add(ref _written, buffer.Length);
    }
}

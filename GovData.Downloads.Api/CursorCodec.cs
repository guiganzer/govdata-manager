using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GovData.Downloads.Api;

public sealed class CursorCodec(byte[] key)
{
    public string Encode(string definition, string partition, string release, long offset, int limit)
    {
        var payload = Encoding.UTF8.GetBytes($"1|{definition}|{partition}|{release}|{offset}|{limit}");
        var mac = HMACSHA256.HashData(key, payload);
        var bytes = new byte[payload.Length + 16];
        payload.CopyTo(bytes, 0);
        mac.AsSpan(0, 16).CopyTo(bytes.AsSpan(payload.Length));
        var value = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (value.Length > 512) throw new InvalidOperationException("Cursor acima do limite.");
        return value;
    }

    public (string Definition, string Partition, string Release, long Offset, int Limit)? Decode(string value)
    {
        if (value.Length is < 32 or > 512 || value.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '_' and not '-'))
            return null;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(padded);
            if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != value)
                return null;
            if (bytes.Length <= 16) return null;
            var payload = bytes.AsSpan(0, bytes.Length - 16);
            var mac = HMACSHA256.HashData(key, payload);
            if (!CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, 16), bytes.AsSpan(bytes.Length - 16)))
                return null;
            var fields = Encoding.UTF8.GetString(payload).Split('|');
            if (fields.Length != 6 || fields[0] != "1" || !long.TryParse(fields[4], out var offset) ||
                !int.TryParse(fields[5], out var limit) || offset < 0 || limit is < 1 or > 50000) return null;
            return (fields[1], fields[2], fields[3], offset, limit);
        }
        catch (FormatException) { return null; }
    }
}


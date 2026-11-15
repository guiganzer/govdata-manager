using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using GovData.Downloads.Server;

namespace GovData.Downloads.Api;

public static class ParquetPageReader
{
    public sealed record PageData(byte[] Body, int Count);

    public static Task<PageData> ReadAsync(string path, PublishedRelease release, PublishedPartition part,
        long start, int limit, ApiOptions options, CursorCodec cursors, CancellationToken ct)
        => Task.Run(() => Read(path, release, part, start, limit, options, cursors, ct), ct);

    private static PageData Read(string path, PublishedRelease release, PublishedPartition part,
        long start, int limit, ApiOptions options, CursorCodec cursors, CancellationToken ct)
    {
        if (start < 0 || start > part.Rows || limit is < 1 or > 50000)
            throw new ArgumentOutOfRangeException(nameof(start));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.QueryTimeoutSeconds));
        var token = timeout.Token;
        using var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        DuckSql.Exec(duck, $"SET threads=1; SET memory_limit='{options.DuckDbMemoryMb}MB'; " +
            "SET temp_directory=''; SET autoinstall_known_extensions=false; SET autoload_known_extensions=false; " +
            "SET preserve_insertion_order=true");
        DuckSql.Exec(duck, $"SET allowed_paths=[{DuckSql.Lit(path)}]; SET enable_external_access=false; SET lock_configuration=true");
        using var cmd = duck.CreateCommand();
        // file_row_number é a posição física validada pelo organizador, não OFFSET nem CAST de _linha.
        // A faixa permite ao DuckDB eliminar grupos Parquet sem ordenar toda a publicação.
        var columns = new[] { "_linha" }.Concat(part.Columns).ToArray();
        cmd.CommandText = $"SELECT {string.Join(',', columns.Select(DuckSql.Id))} " +
            $"FROM read_parquet({DuckSql.Lit(path)}, file_row_number=true) " +
            $"WHERE file_row_number >= {start} AND file_row_number < {checked(start + Math.Min(limit, part.Rows - start))} " +
            "ORDER BY file_row_number";
        using var registration = token.Register(() => { try { cmd.Cancel(); } catch (ObjectDisposedException) { } });
        var rows = new List<byte[]>();
        var used = 2048; // Reserva para envelope, IDs e cursor (IDs validados na abertura dos lotes).
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (!long.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out var line) || line != start + rows.Count + 1)
                    throw new InvalidDataException("Sequência _linha diverge da posição física do Parquet.");
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    for (var i = 0; i < columns.Length; i++)
                    {
                        if (reader.IsDBNull(i)) writer.WriteNull(columns[i]);
                        else writer.WriteString(columns[i], Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                    }
                    writer.WriteEndObject();
                }
                if (buffer.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("Registro excede o limite de 2 MiB.");
                if (used + buffer.Length + 1 > options.MaxPageBytes)
                {
                    if (rows.Count == 0) throw new InvalidDataException("Registro excede o limite de bytes da página.");
                    break;
                }
                rows.Add(buffer.ToArray());
                used += checked((int)buffer.Length + 1);
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        if (rows.Count == 0 && start < part.Rows)
            throw new InvalidDataException("Parquet terminou antes da contagem publicada.");
        var next = start + rows.Count >= part.Rows ? null : cursors.Encode(release.ConjuntoId,
            part.ArquivoId, release.Id, start + rows.Count, limit);
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("conjunto", release.ConjuntoId);
            writer.WriteString("particao", part.ArquivoId);
            writer.WriteString("versao", part.OriginalSha256);
            writer.WriteNumber("inicio", start);
            writer.WriteNumber("limit", limit);
            writer.WriteNumber("quantidade", rows.Count);
            writer.WriteNumber("total", part.Rows);
            writer.WriteString("proximoCursor", next);
            writer.WriteStartArray("registros");
            foreach (var bytes in rows) writer.WriteRawValue(bytes, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (body.Length > options.MaxPageBytes) throw new InvalidDataException("Página excede o limite de bytes.");
        return new(body.ToArray(), rows.Count);
    }
}

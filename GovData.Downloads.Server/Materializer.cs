using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using GovData.Downloads.Core;

namespace GovData.Downloads.Server;

public static class Materializer
{
    public const long SegmentTargetBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public sealed class RowSink : IAsyncDisposable
    {
        private readonly string _folder;
        private FileStream _data;
        private FileStream _index;
        private readonly IncrementalHash _coverage = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly long _maxBytes;
        private readonly long _segmentTargetBytes;
        private readonly ArrayBufferWriter<byte> _rowBuffer = new();
        private readonly ArrayBufferWriter<byte> _pendingData = new();
        private readonly ArrayBufferWriter<byte> _pendingIndex = new();
        private readonly Utf8JsonWriter _rowWriter;
        private const int WriteBatchBytes = 4 * 1024 * 1024;
        private JsonEncodedText[] _encodedColumns = [];
        private long _closedBytes;
        private long _segmentStart;
        private long _segmentRows;
        private bool _completed;
        public long Rows { get; private set; }
        public List<string> Columns { get; private set; } = [];
        public List<PublishedSegment> Segments { get; } = [];
        public string CoverageSha256 { get; private set; } = "";

        public RowSink(string folder, long maxBytes, long segmentTargetBytes = SegmentTargetBytes)
        {
            if (segmentTargetBytes < 2 * 1024 * 1024 || maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(segmentTargetBytes));
            _folder = Path.Combine(folder, "segments");
            Directory.CreateDirectory(_folder);
            _maxBytes = maxBytes;
            _segmentTargetBytes = Math.Min(maxBytes, segmentTargetBytes);
            _rowWriter = new Utf8JsonWriter(_rowBuffer, new JsonWriterOptions { Encoder = Compact.Encoder });
            (_data, _index) = OpenSegment(0);
        }

        private (FileStream Data, FileStream Index) OpenSegment(int number)
        {
            var prefix = Path.Combine(_folder, number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            var data = new FileStream(prefix + ".rows.jsonl", FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, true);
            try
            {
                var index = new FileStream(prefix + ".rows.idx", FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1024 * 1024, true);
                return (data, index);
            }
            catch { data.Dispose(); throw; }
        }

        public void SetColumns(IEnumerable<string> names)
        {
            if (Columns.Count > 0) throw new InvalidOperationException("Cabeçalho duplicado.");
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var raw in names)
            {
                var name = string.IsNullOrEmpty(raw) ? $"_coluna_{Columns.Count + 1}" : raw;
                if (name.StartsWith('_') && name is not ("_origem_aba" or "_origem_linha")) name = "fonte" + name;
                seen.TryGetValue(name, out var count);
                seen[name] = count + 1;
                Columns.Add(count == 0 ? name : $"{name}__{count + 1}");
            }
            if (Columns.Count is 0 or > 4096) throw new InvalidDataException("Número de colunas inválido.");
            _encodedColumns = Columns.Select(name => JsonEncodedText.Encode(name, Compact.Encoder)).ToArray();
        }

        public async Task WriteAsync(IReadOnlyList<string?> values, CancellationToken cancellationToken)
        {
            if (Columns.Count == 0) throw new InvalidOperationException("Cabeçalho ausente.");
            if (_completed) throw new InvalidOperationException("Materialização já concluída.");
            if (Rows % 1024 == 0) cancellationToken.ThrowIfCancellationRequested();
            if (values.Count != Columns.Count)
                throw new InvalidDataException("Linha organizada com largura diferente do contrato.");
            _rowBuffer.Clear();
            _rowWriter.Reset(_rowBuffer);
            _rowWriter.WriteStartObject();
            _rowWriter.WriteString("_linha", (Rows + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (var i = 0; i < Columns.Count; i++)
            {
                var value = values[i];
                if (value is null) _rowWriter.WriteNull(_encodedColumns[i]);
                else _rowWriter.WriteString(_encodedColumns[i], value);
            }
            _rowWriter.WriteEndObject();
            _rowWriter.Flush();
            var bytes = _rowBuffer.WrittenMemory;
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Registro maior que 2 MB.");
            var pendingBytes = _pendingData.WrittenCount + _pendingIndex.WrittenCount;
            if (_closedBytes + _data.Position + _index.Position + pendingBytes + bytes.Length + 9 > _maxBytes)
                throw new IOException("Materialização excede a cota configurada.");
            if (_segmentRows > 0 && _data.Position + _pendingData.WrittenCount + bytes.Length + 1 > _segmentTargetBytes)
            {
                await CloseSegmentAsync(cancellationToken);
                _segmentStart = Rows;
                _segmentRows = 0;
                (_data, _index) = OpenSegment(Segments.Count);
            }
            BinaryPrimitives.WriteInt64LittleEndian(_pendingIndex.GetSpan(8),
                _data.Position + _pendingData.WrittenCount);
            _pendingIndex.Advance(8);
            var rowStart = _pendingData.WrittenCount;
            bytes.Span.CopyTo(_pendingData.GetSpan(bytes.Length));
            _pendingData.Advance(bytes.Length);
            _pendingData.GetSpan(1)[0] = (byte)'\n';
            _pendingData.Advance(1);
            _coverage.AppendData(_pendingData.WrittenSpan.Slice(rowStart, bytes.Length + 1));
            Rows++;
            _segmentRows++;
            if (_pendingData.WrittenCount >= WriteBatchBytes || _pendingIndex.WrittenCount >= 64 * 1024)
                await FlushPendingAsync(cancellationToken);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_completed) throw new InvalidOperationException("Materialização já concluída.");
            await CloseSegmentAsync(cancellationToken);
            _completed = true;
            CoverageSha256 = Convert.ToHexString(_coverage.GetHashAndReset()).ToLowerInvariant();
        }

        private async Task CloseSegmentAsync(CancellationToken cancellationToken)
        {
            await FlushPendingAsync(cancellationToken);
            await _index.FlushAsync(cancellationToken);
            await _data.FlushAsync(cancellationToken);
            _index.Flush(true);
            _data.Flush(true);
            var dataBytes = _data.Position;
            var indexBytes = _index.Position;
            await _index.DisposeAsync();
            await _data.DisposeAsync();
            Segments.Add(new PublishedSegment
            {
                Start = _segmentStart, Rows = _segmentRows,
                DataBytes = dataBytes, IndexBytes = indexBytes
            });
            _closedBytes += dataBytes + indexBytes;
        }

        private async Task FlushPendingAsync(CancellationToken cancellationToken)
        {
            if (_pendingIndex.WrittenCount == 0) return;
            var capacity = StorageProtection.ReadCapacity(_data.Name);
            if (capacity.AvailableBytes - _pendingIndex.WrittenCount - _pendingData.WrittenCount <=
                capacity.TotalBytes / 5)
                throw new IOException("A materialização foi interrompida para manter ao menos 20% do volume livre.");
            await _index.WriteAsync(_pendingIndex.WrittenMemory, cancellationToken);
            await _data.WriteAsync(_pendingData.WrittenMemory, cancellationToken);
            _pendingIndex.Clear();
            _pendingData.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await _index.DisposeAsync();
            await _data.DisposeAsync();
            _rowWriter.Dispose();
            _coverage.Dispose();
        }
    }

}

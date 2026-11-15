using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DuckDB.NET.Data;
using Microsoft.VisualBasic.FileIO;
using DbcToDbf;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;
using ExcelDataReader;

namespace GovData.Downloads.Server;

public sealed record RawRow(string? Member, string? Sheet, long Line, IReadOnlyList<string?> Cells);

public static class DuckSql
{
    public static string Lit(string? s) => s is null ? "NULL" : "'" + s.Replace("'", "''") + "'";
    public static string Id(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    public static void Exec(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public static long Count(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); }
    // Tabelas largas (Censo Escolar: 507 colunas, 220 mil linhas) estouravam o teto fixo de 1 GB. O teto passa a ser
    // 1/4 da memória disponível, entre 2 e 4 GB. settings.json pode fixar o limite;
    // GOVDATA_DUCKDB_MEMORY_MB continua disponível quando a opção está em automático.
    public static string MemoryLimit(int configuredMegabytes = 0)
    {
        if (configuredMegabytes >= 512) return configuredMegabytes + "MB";
        if (int.TryParse(Environment.GetEnvironmentVariable("GOVDATA_DUCKDB_MEMORY_MB"), out var megabytes) && megabytes >= 512)
            return megabytes + "MB";
        var bytes = Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4, 2L << 30, 4L << 30);
        return (bytes >> 20) + "MB";
    }
    public static DuckDBConnection Open(string? tempRoot = null, string? database = null,
        int threads = 1, int memoryMegabytes = 0)
    {
        var c = new DuckDBConnection("DataSource=" + (database ?? ":memory:")); c.Open();
        Exec(c, $"SET autoinstall_known_extensions=false; SET autoload_known_extensions=false; SET memory_limit='{MemoryLimit(memoryMegabytes)}'; SET threads={Math.Clamp(threads, 1, 32)}; SET preserve_insertion_order=false");
        if (tempRoot is not null)
        {
            var temp = Path.Combine(tempRoot, ".duckdb-spill");
            Directory.CreateDirectory(temp);
            Exec(c, $"SET temp_directory={Lit(temp)}");
        }
        return c;
    }
}

public static class RawGrid
{
    public const string Version = "grade-v3-8";
    private static void RequireHeadroom(string path, long plannedBytes = 0)
    {
        var capacity = GovData.Downloads.Core.StorageProtection.ReadCapacity(path);
        if (capacity.AvailableBytes - plannedBytes <= capacity.TotalBytes / 5)
            throw new IOException("A captura foi interrompida para manter 20% do volume livre.");
    }
    public static async Task<string> CaptureAsync(string original, string hash, string arquivo, string format,
        ConjuntoSources sources, string root, CancellationToken ct, Action<string, long, long>? progress = null,
        int batchRows = 4096, int duckDbThreads = 1, int duckDbMemoryMb = 0,
        bool recapture = false, long massiveThresholdBytes = 0)
    {
        if (batchRows is < 100 or > 65536) throw new ArgumentOutOfRangeException(nameof(batchRows));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, hash + ".grid");
        var legacyPath = Path.Combine(root, hash + ".parquet");
        var stamp = Path.Combine(root, hash + ".json");
        // Modo massivo: CSV (solto ou membro de ZIP) acima do limite configurado e lido pelo
        // parser nativo do DuckDB. Abaixo do limite, ou para planilhas/DBF, segue o leitor linha a linha.
        var massive = massiveThresholdBytes > 0 && PlanMassiveCsv(original, format, sources) is { } planned &&
            planned >= massiveThresholdBytes;
        var signature = Version + "|" + sources.Encoding + "|" + sources.Delimiter + "|" +
            sources.FormatoMembro + "|" +
            string.Join("|", sources.Membros ?? []) + (massive ? "|" + MassiveVersion : "");
        if (!recapture && Directory.Exists(path) && Directory.EnumerateFiles(path, "*.parquet").Any() &&
            File.Exists(stamp) && await File.ReadAllTextAsync(stamp, ct) == signature)
        {
            progress?.Invoke("Grade já capturada", 0, 0);
            return path;
        }
        if (!recapture && File.Exists(legacyPath) && File.Exists(stamp) &&
            await File.ReadAllTextAsync(stamp, ct) == signature)
            return legacyPath;
        var chunksRoot = Path.Combine(root, ".capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(chunksRoot);
        try
        {
            RequireHeadroom(root);
            var width = 0;
            long rows = 0;
            if (massive)
            {
                (rows, width) = CaptureMassiveCsv(original, format, sources, arquivo, root, chunksRoot, ct,
                    progress, duckDbThreads, duckDbMemoryMb);
            }
            else
            using (var reader = Read(original, format, sources, ct).GetEnumerator())
            {
                var exhausted = false;
                RawRow? pending = null;
                var chunkNumber = 0;
                while (!exhausted)
                {
                    ct.ThrowIfCancellationRequested();
                    RawRow? first = pending;
                    pending = null;
                    while (first is null && reader.MoveNext())
                        if (!reader.Current.Cells.All(string.IsNullOrEmpty)) first = reader.Current;
                    if (first is null) break;
                    var chunkWidth = first.Cells.Count;
                    if (chunkWidth > 4096) throw new InvalidDataException("Grade excede 4096 colunas.");
                    width = Math.Max(width, chunkWidth);
                    // A grade larga nunca vira JSON nem uma unica tabela DuckDB.
                    // Cada conexao e cada arquivo Parquet permanecem pequenos.
                    // O lote configurado controla o tamanho de cada arquivo largo.
                    // O padrao de 4096 reduz leituras de metadados sem elevar o teto DuckDB.
                    // Tabelas estreitas recebem blocos maiores (orcamento de ~4 milhoes de celulas),
                    // evitando centenas de arquivos e conexoes por arquivo de milhoes de linhas.
                    var chunkLimit = chunkWidth >= 256 ? batchRows
                        : Math.Max(batchRows, Math.Min(262_144, CellBudget / Math.Max(1, chunkWidth)));
                    using var chunkDb = DuckSql.Open(root, threads: duckDbThreads,
                        memoryMegabytes: duckDbMemoryMb);
                    var columns = string.Join(',', Enumerable.Range(1, chunkWidth).Select(i => $"c{i} VARCHAR"));
                    DuckSql.Exec(chunkDb, "CREATE TABLE raw (_arquivo VARCHAR, _membro VARCHAR, _aba VARCHAR, _linha BIGINT" +
                        (columns.Length > 0 ? "," + columns : "") + ")");
                    var chunkRows = 0;
                    using (var appender = chunkDb.CreateAppender("raw"))
                    {
                        var row = first;
                        while (chunkRows < chunkLimit)
                        {
                            if (row.Cells.Count > chunkWidth)
                            {
                                pending = row;
                                break;
                            }
                            var output = appender.CreateRow().AppendValue(arquivo).AppendValue(row.Member)
                                .AppendValue(row.Sheet).AppendValue(row.Line);
                            for (var i = 0; i < chunkWidth; i++)
                                output.AppendValue(i < row.Cells.Count ? row.Cells[i] : null);
                            output.EndRow();
                            chunkRows++;
                            rows++;
                            if (chunkRows % batchRows == 0)
                            {
                                RequireHeadroom(root);
                                progress?.Invoke("Capturando grade", rows, 0);
                            }
                            row = null;
                            while (reader.MoveNext())
                                if (!reader.Current.Cells.All(string.IsNullOrEmpty)) { row = reader.Current; break; }
                            if (row is null) { exhausted = true; break; }
                        }
                        if (row is not null && !exhausted && pending is null) pending = row;
                    }
                    var chunk = Path.Combine(chunksRoot, $"{chunkNumber:D6}.parquet");
                    DuckSql.Exec(chunkDb, $"COPY raw TO {DuckSql.Lit(chunk)} (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE {RowGroupSize(chunkWidth + 4)})");
                    chunkNumber++;
                    progress?.Invoke($"Bloco {chunkNumber} da grade gravado", rows, 0);
                    RequireHeadroom(root);
                }
            }
            if (width == 0) throw new InvalidDataException("Arquivo sem celulas tabulares.");
            progress?.Invoke("Grade Parquet pronta", rows, rows);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) Directory.Delete(path, true);
            Directory.Move(chunksRoot, path);
            await File.WriteAllTextAsync(stamp, signature, ct);
            return path;
        }
        finally
        {
            if (Directory.Exists(chunksRoot)) Directory.Delete(chunksRoot, true);
        }
    }
    public const string MassiveVersion = "massivo-v1";
    private const int CellBudget = 4_194_304;

    // Grupos de linhas proporcionais a largura: ~1 milhao de celulas por grupo.
    // Tabelas largas continuam com grupos pequenos; estreitas deixam de gerar milhares de grupos.
    public static int RowGroupSize(int columns) => Math.Clamp(1_048_576 / Math.Max(1, columns), 2048, 122_880);

    private static bool IsSafeZipEntry(ZipArchiveEntry entry) =>
        !(entry.FullName.Contains("..") || entry.FullName.StartsWith('/') || entry.FullName.Contains(':') ||
          entry.Length > 200L * 1024 * 1024 * 1024 ||
          entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 500);

    private static bool IsSafeArchiveMember(string member) =>
        !(member.Contains("..") || member.StartsWith('/') || member.Contains(':') || member.Contains('\\'));

    /// <summary>Bytes descompactados de CSV a ler, ou null quando a fonte nao e elegivel ao modo massivo.</summary>
    private static long? PlanMassiveCsv(string path, string format, ConjuntoSources source)
    {
        if (format is "csv" or "txt" or "tsv") return new FileInfo(path).Length;
        IEnumerable<(string Name, long Size)> entries;
        if (format == "zip")
        {
            using var zip = ZipFile.OpenRead(path);
            entries = zip.Entries.Select(e => (e.FullName, e.Length)).ToArray();
        }
        else if (format == "7z")
        {
            using var archive = SevenZipArchive.OpenArchive(path, new SharpCompress.Readers.ReaderOptions());
            entries = archive.Entries.Where(e => !e.IsDirectory).Select(e => (e.Key ?? "", e.Size)).ToArray();
        }
        else return null;
        long total = 0;
        var any = false;
        foreach (var (name, size) in entries)
        {
            if (!ConjuntoContract.Matches(name, source.Membros)) continue;
            var memberFormat = source.FormatoMembro ?? ArchiveMemberFormat(name);
            if (memberFormat is null) continue;
            // Um membro nao-CSV (DBF, XLSX) mantem a fonte inteira no leitor linha a linha.
            if (memberFormat is not ("csv" or "txt" or "tsv")) return null;
            total += size;
            any = true;
        }
        return any ? total : null;
    }

    private static (long Rows, int Width) CaptureMassiveCsv(string original, string format, ConjuntoSources source,
        string arquivo, string root, string chunksRoot, CancellationToken ct, Action<string, long, long>? progress,
        int threads, int memoryMb)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var utf8 = Encoding.GetEncoding(source.Encoding).CodePage == 65001;
        long rows = 0;
        var width = 0;
        var number = 0;

        void Ingest(string csv, string? member)
        {
            {
                using var c = DuckSql.Open(root, null, threads, memoryMb);
                // Com a ordem de insercao preservada, row_number() OVER () e calculado em fluxo,
                // na ordem do arquivo. Medido: 2,4x mais rapido que tabela intermediaria + rowid,
                // sem tabela em memoria/disco, e correto de 1 a 8 threads.
                DuckSql.Exec(c, "SET preserve_insertion_order=true");
                progress?.Invoke("Lendo CSV com DuckDB (modo massivo)", rows, 0);
                var options = $"delim={DuckSql.Lit(source.Delimiter)}, header=false, all_varchar=true, " +
                    "quote='\"', escape='\"', null_padding=true, auto_detect=true, encoding='utf-8'";
                var reader = $"read_csv({DuckSql.Lit(csv)}, {options})";
                var names = new List<string>();
                using (var schema = c.CreateCommand())
                {
                    schema.CommandText = $"SELECT * FROM {reader} LIMIT 0";
                    using var r = schema.ExecuteReader();
                    for (var i = 0; i < r.FieldCount; i++) names.Add(r.GetName(i));
                }
                if (names.Count > 4096) throw new InvalidDataException("Grade excede 4096 colunas.");
                if (names.Count == 0) return;
                width = Math.Max(width, names.Count);
                var cells = string.Join(',', names.Select((n, i) => $"nullif({DuckSql.Id(n)},'') AS c{i + 1}"));
                var notEmpty = string.Join(" OR ", names.Select(n => $"coalesce({DuckSql.Id(n)},'')<>''"));
                var chunk = Path.Combine(chunksRoot, $"{number:D6}.parquet");
                progress?.Invoke("Gravando grade Parquet (modo massivo)", rows, 0);
                DuckSql.Exec(c, $"COPY (SELECT {DuckSql.Lit(arquivo)} AS _arquivo, CAST({DuckSql.Lit(member)} AS VARCHAR) AS _membro, " +
                    $"NULL::VARCHAR AS _aba, _registro AS _linha, {cells} FROM " +
                    $"(SELECT CAST(row_number() OVER () AS BIGINT) AS _registro, * FROM {reader}) WHERE {notEmpty}) " +
                    $"TO {DuckSql.Lit(chunk)} (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE {RowGroupSize(names.Count + 4)})");
                rows += DuckSql.Count(c, $"SELECT count(*) FROM read_parquet({DuckSql.Lit(chunk)})");
                number++;
                progress?.Invoke($"Bloco {number} da grade gravado", rows, 0);
            }
            RequireHeadroom(root);
        }

        string Transcode(Stream input, long expected)
        {
            // DuckDB nao le dentro de ZIP nem Windows-1252: o membro e convertido em fluxo para UTF-8.
            var target = Path.Combine(chunksRoot, $".member-{number:D6}.csv");
            RequireHeadroom(chunksRoot, expected);
            using var reader = new StreamReader(input, Encoding.GetEncoding(source.Encoding), true, 1 << 20);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            using var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 20);
            var buffer = new char[1 << 20];
            long done = 0, next = 0;
            int n;
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, n);
                done += n;
                if (done >= next)
                {
                    progress?.Invoke("Extraindo membro para leitura massiva", done, expected);
                    RequireHeadroom(target);
                    next = done + (256L << 20);
                }
            }
            return target;
        }

        if (format == "zip")
        {
            using var zip = ZipFile.OpenRead(original);
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!ConjuntoContract.Matches(entry.FullName, source.Membros)) continue;
                if (source.FormatoMembro is null && ArchiveMemberFormat(entry.FullName) is null) continue;
                if (!IsSafeZipEntry(entry)) throw new InvalidDataException("Membro ZIP inseguro ou expansão excessiva.");
                string temp;
                using (var input = entry.Open()) temp = Transcode(input, entry.Length);
                try { Ingest(temp, entry.FullName); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        else if (format == "7z")
        {
            using var archive = SevenZipArchive.OpenArchive(original, new SharpCompress.Readers.ReaderOptions());
            using var reader = archive.ExtractAllEntries();
            long total = 0;
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                var member = reader.Entry.Key ?? "";
                if (!IsSafeArchiveMember(member)) throw new InvalidDataException("Membro 7z inseguro.");
                total = checked(total + reader.Entry.Size);
                if (total > 200L * 1024 * 1024 * 1024) throw new InvalidDataException("7z excede a cota descompactada.");
                if (!ConjuntoContract.Matches(member, source.Membros)) continue;
                if (source.FormatoMembro is null && ArchiveMemberFormat(member) is null) continue;
                string temp;
                using (var input = reader.OpenEntryStream()) temp = Transcode(input, reader.Entry.Size);
                try { Ingest(temp, member); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        else if (utf8) Ingest(original, null);
        else
        {
            string temp;
            using (var input = File.OpenRead(original)) temp = Transcode(input, input.Length);
            try { Ingest(temp, null); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return (rows, width);
    }

    public static IEnumerable<RawRow> Read(string path, string format, ConjuntoSources source, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (format == "xlsx") { foreach (var row in ReadXlsx(path, ct)) yield return row; yield break; }
        if (format == "xls") { foreach (var row in ReadXls(path, ct)) yield return row; yield break; }
        if (format == "dbf") { foreach (var row in ReadDbf(path, source.Encoding, ct)) yield return row; yield break; }
        if (format == "dbc")
        {
            var dbf = Path.Combine(Path.GetDirectoryName(path)!, ".dbc-" + Guid.NewGuid().ToString("N") + ".dbf");
            try
            {
                RequireHeadroom(dbf);
                new DbcDecompressor().Decompress(path, dbf);
                if (new FileInfo(dbf).Length > 200L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("DBC excede o limite descompactado.");
                foreach (var row in ReadDbf(dbf, source.Encoding, ct)) yield return row;
            }
            finally { if (File.Exists(dbf)) File.Delete(dbf); }
            yield break;
        }
        if (format == "zip")
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                if (!ConjuntoContract.Matches(entry.FullName, source.Membros)) continue;
                if (source.FormatoMembro is null && ArchiveMemberFormat(entry.FullName) is null) continue;
                if (!IsSafeZipEntry(entry))
                    throw new InvalidDataException("Membro ZIP inseguro ou expansão excessiva.");
                using var stream = entry.Open();
                foreach (var row in ReadArchiveMember(stream, entry.FullName, source, path, ct)) yield return row;
            }
            yield break;
        }
        if (format == "7z")
        {
            using var archive = SevenZipArchive.OpenArchive(path, new SharpCompress.Readers.ReaderOptions());
            using var reader = archive.ExtractAllEntries();
            long total = 0;
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                var member = reader.Entry.Key ?? "";
                if (!IsSafeArchiveMember(member))
                    throw new InvalidDataException("Membro 7z inseguro.");
                total = checked(total + reader.Entry.Size);
                if (total > 200L * 1024 * 1024 * 1024) throw new InvalidDataException("7z excede a cota descompactada.");
                if (!ConjuntoContract.Matches(member, source.Membros)) continue;
                if (source.FormatoMembro is null && ArchiveMemberFormat(member) is null) continue;
                using var stream = reader.OpenEntryStream();
                foreach (var row in ReadArchiveMember(stream, member, source, path, ct)) yield return row;
            }
            yield break;
        }
        if (format is not ("csv" or "txt" or "tsv")) throw new NotSupportedException("Leitor de grade não homologado: " + format);
        using var input = File.OpenRead(path);
        foreach (var row in ReadCsv(input, source, null, ct)) yield return row;
    }
    private static IEnumerable<RawRow> ReadArchiveMember(Stream stream, string member,
        ConjuntoSources source, string archive, CancellationToken ct)
    {
        var format = source.FormatoMembro ?? ArchiveMemberFormat(member)
            ?? throw new NotSupportedException("Formato tabular de membro não homologado: " + member);
        if (format is "csv" or "txt" or "tsv")
        {
            foreach (var row in ReadCsv(stream, source, member, ct)) yield return row;
            yield break;
        }
        var staged = Path.Combine(Path.GetDirectoryName(archive)!, ".member-" + Guid.NewGuid().ToString("N") + "." + format);
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 1024]; long copied = 0; int n;
                while ((n = stream.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    copied += n;
                    if (copied > 200L * 1024 * 1024 * 1024) throw new InvalidDataException("Membro excede a cota descompactada.");
                    if (copied % (8L * 1024 * 1024) < n) RequireHeadroom(staged, n);
                    output.Write(buffer, 0, n);
                }
            }
            foreach (var row in Read(staged, format, source, ct))
                yield return row with { Member = member };
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static string? ArchiveMemberFormat(string member)
    {
        var fileName = Path.GetFileName(member);
        if (fileName.EndsWith("csv", StringComparison.OrdinalIgnoreCase)) return "csv";
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension is "txt" or "tsv" or "xlsx" or "xls" or "dbf" or "dbc" ? extension : null;
    }

    private static IEnumerable<RawRow> ReadCsv(Stream stream, ConjuntoSources source, string? member, CancellationToken ct)
    {
        using var csv = new TextFieldParser(stream, Encoding.GetEncoding(source.Encoding), true, true)
            { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        csv.SetDelimiters(source.Delimiter);
        while (!csv.EndOfData)
        {
            ct.ThrowIfCancellationRequested();
            var line = csv.LineNumber;
            var values = csv.ReadFields()!;
            yield return new(member, null, line, values.Select(v => v.Length == 0 ? null : v).ToArray());
        }
    }
    private static IEnumerable<RawRow> ReadDbf(string path, string encoding, CancellationToken ct)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        var head = reader.ReadBytes(32);
        var records = BitConverter.ToUInt32(head, 4); var header = BitConverter.ToUInt16(head, 8); var length = BitConverter.ToUInt16(head, 10);
        var fields = new List<(string Name, int Length)>();
        while (stream.Position < header - 1)
        {
            var field = reader.ReadBytes(32); if (field.Length != 32) throw new InvalidDataException("Cabeçalho DBF incompleto.");
            fields.Add((Encoding.ASCII.GetString(field, 0, 11).TrimEnd('\0'), field[16]));
        }
        if (length != 1 + fields.Sum(f => f.Length)) throw new InvalidDataException("Largura DBF inválida.");
        yield return new(null, null, 1, fields.Select(f => (string?)f.Name).ToArray());
        stream.Position = header; var enc = Encoding.GetEncoding(encoding);
        for (long n = 0; n < records; n++)
        {
            ct.ThrowIfCancellationRequested(); var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("Registro DBF truncado.");
            if (bytes[0] == '*') continue;
            if (bytes[0] != ' ') throw new InvalidDataException("Registro DBF inválido.");
            var offset = 1; var values = new List<string?>();
            foreach (var f in fields) { var value = enc.GetString(bytes, offset, f.Length).TrimEnd(' '); values.Add(value.Length == 0 ? null : value); offset += f.Length; }
            yield return new(null, null, n + 2, values);
        }
    }
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static IEnumerable<RawRow> ReadXls(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
        do
        {
            var sheet = reader.Name;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.FieldCount > 4096) throw new InvalidDataException("Planilha XLS excede 4096 colunas.");
                var cells = new List<string?>(reader.FieldCount);
                for (var col = 0; col < reader.FieldCount; col++)
                {
                    var value = reader.GetValue(col);
                    cells.Add(value switch
                    {
                        null => null,
                        double number => number.ToString("R", CultureInfo.InvariantCulture),
                        float number => number.ToString("R", CultureInfo.InvariantCulture),
                        DateTime date => date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                        bool boolean => boolean ? "true" : "false",
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                    });
                }
                while (cells.Count > 0 && cells[^1] is null) cells.RemoveAt(cells.Count - 1);
                if (cells.Count > 0) yield return new(null, sheet, reader.Depth + 1, cells);
            }
        } while (reader.NextResult());
    }
    public static IEnumerable<RawRow> ReadXlsx(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument Xml(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("XLSX sem " + name);
            using var input = entry.Open(); using var reader = XmlReader.Create(input, SafeXml()); return XDocument.Load(reader);
        }
        var book = Xml("xl/workbook.xml");
        var rels = Xml("xl/_rels/workbook.xml.rels");
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Xml("xl/sharedStrings.xml").Descendants(Ns + "si")
            .Select(e => string.Concat(e.Descendants(Ns + "t").Select(x => x.Value))).ToArray();
        var dateStyles = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is not null)
        {
            var styles = Xml("xl/styles.xml");
            var custom = styles.Descendants(Ns + "numFmt").Where(e => ((string?)e.Attribute("formatCode") ?? "").Contains('y', StringComparison.OrdinalIgnoreCase))
                .Select(e => (int?)e.Attribute("numFmtId") ?? 0).ToHashSet();
            var i = 0;
            foreach (var xf in styles.Descendants(Ns + "cellXfs").Elements(Ns + "xf"))
            { var fmt = (int?)xf.Attribute("numFmtId") ?? 0; if (fmt is 14 or 15 or 16 or 17 or 22 || custom.Contains(fmt)) dateStyles.Add(i); i++; }
        }
        var date1904 = (string?)book.Descendants(Ns + "workbookPr").FirstOrDefault()?.Attribute("date1904") is "1" or "true";
        foreach (var sheet in book.Descendants(Ns + "sheet"))
        {
            var name = (string)sheet.Attribute("name")!; var id = (string)sheet.Attribute(rel + "id")!;
            var target = (string?)rels.Descendants().Single(e => (string?)e.Attribute("Id") == id).Attribute("Target") ?? "";
            if (target.Contains("..") || target.Contains(':')) throw new InvalidDataException("Caminho de aba inseguro.");
            target = target.TrimStart('/'); if (!target.StartsWith("xl/")) target = "xl/" + target;
            var entry = zip.GetEntry(target) ?? throw new InvalidDataException("Aba ausente.");
            using var content = entry.Open(); using var xml = XmlReader.Create(content, SafeXml());
            long line = 0;
            while (!xml.EOF)
            {
                ct.ThrowIfCancellationRequested();
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "row") { xml.Read(); continue; }
                line = long.TryParse(xml.GetAttribute("r"), out var num) ? num : line + 1;
                var row = XElement.Parse(xml.ReadOuterXml()); var cells = new List<string?>();
                foreach (var cell in row.Elements(Ns + "c"))
                {
                    var col = 0;
                    foreach (var ch in (string?)cell.Attribute("r") ?? "") { if (ch is < 'A' or > 'Z') break; col = col * 26 + ch - 'A' + 1; }
                    if (col is < 1 or > 4096) throw new InvalidDataException("Coluna XLSX inválida.");
                    while (cells.Count < col) cells.Add(null);
                    var raw = (string?)cell.Element(Ns + "v"); var type = (string?)cell.Attribute("t"); string? value = raw;
                    if (type == "s") value = shared[int.Parse(raw!, CultureInfo.InvariantCulture)];
                    else if (type == "inlineStr") value = string.Concat(cell.Descendants(Ns + "t").Select(t => t.Value));
                    else if (type == "b") value = raw == "1" ? "true" : "false";
                    else if (type != "e" && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        if (int.TryParse((string?)cell.Attribute("s"), out var style) && dateStyles.Contains(style))
                        {
                            var date = date1904 ? new DateTime(1904,1,1).AddDays(number) : DateTime.FromOADate(number);
                            value = date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                        }
                        else value = number.ToString("R", CultureInfo.InvariantCulture);
                    }
                    cells[col - 1] = string.IsNullOrEmpty(value) ? null : value;
                }
                while (cells.Count > 0 && cells[^1] is null) cells.RemoveAt(cells.Count - 1);
                if (cells.Count > 0) yield return new(null, name, line, cells);
            }
        }
    }
    private static XmlReaderSettings SafeXml() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
}

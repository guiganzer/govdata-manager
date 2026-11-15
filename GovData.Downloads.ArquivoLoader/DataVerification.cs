using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Npgsql;

namespace GovData.Downloads.ArquivoLoader;

internal static partial class Program
{
    private sealed record VerificationPartition(
        string PublicationId,
        string ConjuntoId,
        string PublicationStatus,
        string TableName,
        string[] Columns,
        string PartitionId,
        string RelativePath,
        long ExpectedRows,
        long RowsLoaded,
        long LastLine,
        string Status);

    private static async Task<int> VerifyDataAsync(NpgsqlConnection pg, LoaderConfig config, string publicationId)
    {
        if (!Regex.IsMatch(publicationId, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("--publication deve ser um ID hexadecimal minúsculo com 64 caracteres.");

        var partitions = new List<VerificationPartition>();
        await using (var command = new NpgsqlCommand("""
            SELECT pub.publication_id, pub.conjunto_id, pub.status, c.table_name, c.columns,
                   part.partition_id, part.relative_path, part.expected_rows,
                   part.rows_loaded, part.last_line, part.status
            FROM arquivo.publicacao AS pub
            JOIN arquivo.conjunto AS c
              ON c.conjunto_id=pub.conjunto_id
             AND c.definition_version=pub.definition_version
            JOIN arquivo.particao AS part ON part.publication_id=pub.publication_id
            WHERE pub.publication_id=@publication_id
            ORDER BY part.partition_id
            """, pg))
        {
            command.Parameters.AddWithValue("publication_id", publicationId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                partitions.Add(new VerificationPartition(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetFieldValue<string[]>(4), reader.GetString(5), reader.GetString(6),
                    reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10)));
            }
        }

        if (partitions.Count == 0)
        {
            Console.WriteLine($"DIVERGÊNCIA: publicação {publicationId} não está cadastrada com partições no schema arquivo.");
            return 2;
        }

        var publicationStatus = partitions[0].PublicationStatus;
        var issues = 0;
        if (publicationStatus != "CARREGADA")
        {
            Console.WriteLine($"DIVERGÊNCIA {publicationId}: status da publicação={publicationStatus}; esperado=CARREGADA.");
            issues++;
        }

        foreach (var partition in partitions)
        {
            if (partition.Status != "CARREGADA" || partition.RowsLoaded != partition.ExpectedRows ||
                partition.LastLine != partition.ExpectedRows)
            {
                Console.WriteLine($"DIVERGÊNCIA {partition.ConjuntoId}/{partition.PartitionId}: " +
                    $"status={partition.Status}; linhas={partition.RowsLoaded}/{partition.ExpectedRows}; " +
                    $"checkpoint={partition.LastLine}.");
                issues++;
                continue;
            }

            if (!SafeIdentifier.IsMatch(partition.TableName) || partition.Columns.Length == 0 ||
                partition.Columns.Any(c => !SafeIdentifier.IsMatch(c) || c is "publicacao_id" or "particao_id" or "_linha") ||
                partition.Columns.Distinct(StringComparer.Ordinal).Count() != partition.Columns.Length)
            {
                Console.WriteLine($"DIVERGÊNCIA {partition.ConjuntoId}/{partition.PartitionId}: nomes de tabela ou colunas inválidos.");
                issues++;
                continue;
            }

            try
            {
                var mismatch = await ComparePartitionAsync(pg, config, partition);
                if (mismatch is null)
                    Console.WriteLine($"VERIFICADA {partition.ConjuntoId}/{partition.PartitionId}: " +
                        $"{partition.ExpectedRows:N0} linhas; fingerprint SHA-256 de todas as colunas coincide com o Parquet.");
                else
                {
                    Console.WriteLine($"DIVERGÊNCIA {partition.ConjuntoId}/{partition.PartitionId}: {mismatch}");
                    issues++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DIVERGÊNCIA {partition.ConjuntoId}/{partition.PartitionId}: {ex.Message}");
                issues++;
            }
        }

        Console.WriteLine($"Verificação de conteúdo concluída: publicação={publicationId}; partições={partitions.Count}; divergências={issues}.");
        Console.WriteLine("Fingerprint criptográfico em fluxo, sem API e sem depender da ordem física das linhas.");
        return issues == 0 ? 0 : 2;
    }

    private static async Task<string?> ComparePartitionAsync(
        NpgsqlConnection pg, LoaderConfig config, VerificationPartition partition)
    {
        if (Path.IsPathRooted(partition.RelativePath) ||
            partition.RelativePath.Split(['/', '\\']).Any(x => x is ".." or "." or ""))
            throw new InvalidDataException("caminho relativo do Parquet inválido.");
        var parquet = ResolveParquetPath(config.ParquetRoot, partition.RelativePath);
        if (!File.Exists(parquet)) throw new FileNotFoundException("Parquet não encontrado.", parquet);

        var expectedSchema = new[]
            { (Name: "publicacao_id", Type: "text"), (Name: "particao_id", Type: "text"), (Name: "_linha", Type: "bigint") }
            .Concat(partition.Columns.Select(x => (Name: x, Type: "text"))).ToArray();
        var physicalSchema = await ReadPhysicalColumnsAsync(pg, partition.TableName);
        if (!physicalSchema.SequenceEqual(expectedSchema))
            throw new InvalidDataException("colunas ou tipos físicos da tabela divergem do cadastro.");

        Directory.CreateDirectory(config.DuckDb.TempDirectory);
        using var duck = new DuckDBConnection("DataSource=:memory:");
        duck.Open();
        ExecuteDuck(duck, $"SET threads={config.DuckDb.Threads.ToString(CultureInfo.InvariantCulture)}");
        ExecuteDuck(duck, $"SET memory_limit={DuckLiteral(config.DuckDb.MemoryLimit)}");
        ExecuteDuck(duck, "SET preserve_insertion_order=true");
        ExecuteDuck(duck, $"SET temp_directory={DuckLiteral(Path.GetFullPath(config.DuckDb.TempDirectory))}");

        var columns = string.Join(",", partition.Columns.Select(QuoteIdentifier));
        using var sourceCommand = duck.CreateCommand();
        sourceCommand.CommandText = $"SELECT CAST({QuoteIdentifier("_linha")} AS BIGINT) AS {QuoteIdentifier("_linha")},{columns} " +
            $"FROM read_parquet({DuckLiteral(parquet)})";
        sourceCommand.UseStreamingMode = true;
        using var source = sourceCommand.ExecuteReader(CommandBehavior.SequentialAccess);
        if (source.FieldCount != partition.Columns.Length + 1)
            throw new InvalidDataException($"quantidade de colunas Parquet={source.FieldCount}; esperada={partition.Columns.Length + 1}.");
        if (source.GetName(0) != "_linha")
            throw new InvalidDataException($"coluna inicial do Parquet={source.GetName(0)}; esperada=_linha.");
        for (var i = 0; i < partition.Columns.Length; i++)
            if (!string.Equals(source.GetName(i + 1), partition.Columns[i], StringComparison.Ordinal))
                throw new InvalidDataException($"coluna Parquet divergente na posição {i + 1}: " +
                    $"recebida={source.GetName(i + 1)}; esperada={partition.Columns[i]}.");

        // A agregação é independente da ordem física. Forçamos uma varredura
        // sequencial para evitar ordenação e arquivos temporários grandes no PostgreSQL.
        foreach (var setting in new[]
        {
            "SET enable_indexscan=off", "SET enable_bitmapscan=off",
            "SET max_parallel_workers_per_gather=0", "SET jit=off"
        })
        {
            await using var settingCommand = new NpgsqlCommand(setting, pg);
            await settingCommand.ExecuteNonQueryAsync();
        }

        var sourceFingerprint = new ContentFingerprint();
        long sourceRows = 0;
        while (source.Read())
        {
            sourceRows++;
            var line = Convert.ToInt64(source.GetValue(0), CultureInfo.InvariantCulture);
            if (line != sourceRows)
                return $"sequência _linha do Parquet interrompida: esperado={sourceRows:N0}; recebido={line:N0}.";
            sourceFingerprint.BeginRow(line, partition.Columns.Length);
            for (var i = 0; i < partition.Columns.Length; i++)
                sourceFingerprint.AppendText(source.IsDBNull(i + 1) ? null : source.GetString(i + 1));
            sourceFingerprint.EndRow(line);

            if (sourceRows % 1_000_000 == 0)
            {
                Console.WriteLine($"  {partition.PartitionId}: Parquet {sourceRows:N0}/{partition.ExpectedRows:N0} linhas.");
                Console.Out.Flush();
            }
        }
        if (sourceRows != partition.ExpectedRows)
            return $"contagem do Parquet={sourceRows:N0}; esperada={partition.ExpectedRows:N0}.";

        var targetColumns = string.Join(",", new[] { "_linha" }.Concat(partition.Columns).Select(QuoteIdentifier));
        await using var targetCommand = new NpgsqlCommand(
            $"SELECT {targetColumns} FROM arquivo.{QuoteIdentifier(partition.TableName)} " +
            "WHERE publicacao_id=@publication_id AND particao_id=@partition_id", pg);
        targetCommand.Parameters.AddWithValue("publication_id", partition.PublicationId);
        targetCommand.Parameters.AddWithValue("partition_id", partition.PartitionId);
        await using var target = await targetCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

        var targetFingerprint = new ContentFingerprint();
        long targetRows = 0;
        while (await target.ReadAsync())
        {
            targetRows++;
            var line = target.GetInt64(0);
            if (line < 1 || line > partition.ExpectedRows)
                return $"_linha fora do intervalo no PostgreSQL: {line:N0}; esperado entre 1 e {partition.ExpectedRows:N0}.";
            targetFingerprint.BeginRow(line, partition.Columns.Length);
            for (var i = 0; i < partition.Columns.Length; i++)
                targetFingerprint.AppendText(await target.IsDBNullAsync(i + 1) ? null : target.GetString(i + 1));
            targetFingerprint.EndRow(line);

            if (targetRows % 1_000_000 == 0)
            {
                Console.WriteLine($"  {partition.PartitionId}: PostgreSQL {targetRows:N0}/{partition.ExpectedRows:N0} linhas.");
                Console.Out.Flush();
            }
        }
        if (targetRows != partition.ExpectedRows)
            return $"contagem no PostgreSQL={targetRows:N0}; esperada={partition.ExpectedRows:N0}.";
        if (sourceFingerprint.MinLine != targetFingerprint.MinLine ||
            sourceFingerprint.MaxLine != targetFingerprint.MaxLine)
            return $"intervalo _linha divergente: Parquet={sourceFingerprint.MinLine:N0}..{sourceFingerprint.MaxLine:N0}; " +
                $"PostgreSQL={targetFingerprint.MinLine:N0}..{targetFingerprint.MaxLine:N0}.";
        if (!sourceFingerprint.HasSameContent(targetFingerprint))
            return "fingerprint das linhas diverge entre o Parquet e o PostgreSQL.";
        return null;
    }

    private sealed class ContentFingerprint
    {
        private readonly ArrayBufferWriter<byte> _row = new(4096);
        private readonly byte[] _digest = new byte[32];
        private readonly byte[] _xor = new byte[32];
        private readonly byte[] _sum = new byte[32];

        public long Count { get; private set; }
        public long MinLine { get; private set; } = long.MaxValue;
        public long MaxLine { get; private set; } = long.MinValue;

        public void BeginRow(long line, int columnCount)
        {
            _row.Clear();
            AppendInt64(line);
            AppendInt32(columnCount);
        }

        public void AppendText(string? value)
        {
            var marker = _row.GetSpan(1);
            marker[0] = value is null ? (byte)0 : (byte)1;
            _row.Advance(1);
            if (value is null) return;

            var byteCount = Encoding.UTF8.GetByteCount(value);
            AppendInt32(byteCount);
            var target = _row.GetSpan(byteCount);
            var written = Encoding.UTF8.GetBytes(value.AsSpan(), target[..byteCount]);
            _row.Advance(written);
        }

        public void EndRow(long line)
        {
            SHA256.HashData(_row.WrittenSpan, _digest);
            for (var i = 0; i < _digest.Length; i++) _xor[i] ^= _digest[i];

            var carry = 0;
            for (var i = _sum.Length - 1; i >= 0; i--)
            {
                var value = _sum[i] + _digest[i] + carry;
                _sum[i] = (byte)value;
                carry = value >> 8;
            }

            Count++;
            MinLine = Math.Min(MinLine, line);
            MaxLine = Math.Max(MaxLine, line);
        }

        public bool HasSameContent(ContentFingerprint other) =>
            Count == other.Count && _xor.AsSpan().SequenceEqual(other._xor) && _sum.AsSpan().SequenceEqual(other._sum);

        private void AppendInt32(int value)
        {
            var span = _row.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32BigEndian(span, value);
            _row.Advance(sizeof(int));
        }

        private void AppendInt64(long value)
        {
            var span = _row.GetSpan(sizeof(long));
            BinaryPrimitives.WriteInt64BigEndian(span, value);
            _row.Advance(sizeof(long));
        }
    }
}

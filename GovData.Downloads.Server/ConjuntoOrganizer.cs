using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;

namespace GovData.Downloads.Server;

public static class ConjuntoOrganizer
{
    public const string Version = "conjuntos-v3-1";
    public static async Task MaterializeAsync(string grid, string arquivo, ConjuntoDefinition d, string folder,
        string home, PublishedPartition part, CancellationToken ct, bool derived = false,
        int duckDbThreads = 1, int duckDbMemoryMb = 0,
        Action<string, long, long>? progress = null, bool includeApiCompatibilityArtifacts = true,
        long maxApiMaterializedBytes = long.MaxValue)
    {
        var capacity = GovData.Downloads.Core.StorageProtection.ReadCapacity(folder);
        var gradeFiles = Directory.Exists(grid)
            ? Directory.EnumerateFiles(grid, "*.parquet").Order(StringComparer.Ordinal).ToArray()
            : [grid];
        if (gradeFiles.Length == 0 || gradeFiles.Any(file => !File.Exists(file)))
            throw new InvalidDataException("Grade Parquet ausente ou vazia.");
        var inputBytes = gradeFiles.Sum(file => new FileInfo(file).Length);
        if (capacity.AvailableBytes - checked(inputBytes * 2) <= capacity.TotalBytes / 5)
            throw new IOException("Espaço insuficiente para organizar a grade mantendo 20% do volume livre.");
        using var c = DuckSql.Open(folder, threads: duckDbThreads, memoryMegabytes: duckDbMemoryMb);
        progress?.Invoke("Organizando (DuckDB)", 0, 0);
        var report = new QualityReport();
        var parquet = Path.Combine(folder, "organized.parquet");
        var gradeList = string.Join(',', gradeFiles.Select(DuckSql.Lit));
        if (derived) DuckSql.Exec(c, $"CREATE VIEW base AS SELECT * FROM read_parquet([{gradeList}], union_by_name=true)");
        else DuckSql.Exec(c, $"CREATE VIEW grade AS SELECT * REPLACE ({DuckSql.Lit(arquivo)} AS _arquivo) FROM read_parquet([{gradeList}], union_by_name=true)");
        foreach (var reference in d.Referencias)
        {
            var path = Path.GetFullPath(Path.Combine(home, reference.Value));
            var expected = Path.GetFullPath(Path.Combine(home, "server", "referencias")) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Referência fora da pasta permitida.");
            var table = "ref_" + reference.Key;
            DuckSql.Exec(c, $"CREATE TABLE {DuckSql.Id(table)} AS SELECT chave,valor FROM read_csv({DuckSql.Lit(path)},header=true,all_varchar=true)");
            if (DuckSql.Count(c, $"SELECT count(*) FROM (SELECT chave FROM {DuckSql.Id(table)} GROUP BY chave HAVING count(*)>1)") > 0)
                throw new InvalidDataException("Chaves duplicadas na referência " + reference.Key);
            DuckSql.Exec(c, $"CREATE MACRO {DuckSql.Id(reference.Key)}(x) AS (SELECT valor FROM {DuckSql.Id(table)} WHERE chave=x)");
        }
        // Organizador CLI (sem artefatos da API legada): o resultado vai direto da grade para o Parquet,
        // sem tabela intermediaria; as validacoes leem o Parquet colunar em uma unica varredura.
        var streaming = !derived && !includeApiCompatibilityArtifacts;
        if (streaming) DuckSql.Exec(c, "SET preserve_insertion_order=true");
        DuckSql.Exec(c, $"SET allowed_paths=[{gradeList},{DuckSql.Lit(parquet)}]; SET enable_external_access=false; SET lock_configuration=true");
        if (derived)
        {
            DuckSql.Exec(c, "CREATE TABLE organized AS " + d.Consulta);
            var columns = string.Join(',', d.Saida.Select(s => $"cast({DuckSql.Id(s)} AS VARCHAR) AS {DuckSql.Id(s)}"));
            DuckSql.Exec(c, $"CREATE TABLE result AS SELECT {columns},NULL::VARCHAR AS _origem_aba,NULL::VARCHAR AS _origem_linha FROM organized");
        }
        else
        {
            var physical = new List<string>();
            using (var schema = c.CreateCommand())
            {
                schema.CommandText = "SELECT * FROM grade LIMIT 0"; using var r = schema.ExecuteReader();
                for (var i = 0; i < r.FieldCount; i++) if (r.GetName(i).StartsWith('c')) physical.Add(r.GetName(i));
            }
            var groups = new List<(string? Member, string? Sheet)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT _membro,_aba FROM grade ORDER BY _membro NULLS FIRST,_aba NULLS FIRST";
                using var r = cmd.ExecuteReader();
                while (r.Read()) groups.Add((r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
            }
            var queries = new List<string>();
            var chosen = new List<(string? Member,string? Sheet,string Where)>();
            foreach (var g in groups)
            {
                if (!ConjuntoContract.Matches(g.Member ?? "", d.Fontes!.Membros) || !ConjuntoContract.Matches(g.Sheet ?? "", d.Fontes.Abas)) continue;
                var where = $"_membro IS NOT DISTINCT FROM {DuckSql.Lit(g.Member)} AND _aba IS NOT DISTINCT FROM {DuckSql.Lit(g.Sheet)}";
                var header = d.Cabecalho!.SemCabecalho ? 0 : d.Cabecalho.Linha ?? 0;
                if (header == 0 && !d.Cabecalho.SemCabecalho)
                {
                    var markers = string.Join(',', d.Cabecalho.ContemUmDe!.Select(x => DuckSql.Lit(x.Trim().ToLowerInvariant())));
                    header = checked((int)DuckSql.Count(c, $"SELECT coalesce(min(_linha),0) FROM grade WHERE {where} AND (" +
                        string.Join(" OR ", physical.Select(p => $"lower(trim({p})) IN ({markers})")) + ")"));
                }
                if (header == 0 && !d.Cabecalho.SemCabecalho)
                {
                    report.Erros.Add($"Cabeçalho ausente: {arquivo}/{g.Member}/{g.Sheet}");
                    continue;
                }
                var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                if (header > 0)
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = $"SELECT {string.Join(',',physical)} FROM grade WHERE {where} AND _linha={header}";
                    using var r = cmd.ExecuteReader();
                    if (!r.Read()) { report.Erros.Add($"Cabeçalho ausente: {arquivo}/{g.Member}/{g.Sheet}"); continue; }
                    for (var i = 0; i < physical.Count; i++)
                        if (!r.IsDBNull(i)) headers.TryAdd(r.GetString(i).Trim(), physical[i]);
                }
                var projection = new List<string>();
                foreach (var col in d.Colunas)
                {
                    var synonym = col.Value.FirstOrDefault(s => headers.ContainsKey(s.Trim()) ||
                        System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), "^c[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                        physical.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase));
                    if (synonym is null)
                    {
                        var msg = $"Coluna ausente: {arquivo}/{g.Sheet}/{col.Key}";
                        (d.Validacoes.ColunasPresentes.Contains(col.Key) || d.Validacoes.Obrigatorias.Contains(col.Key)
                            ? report.Erros : report.Avisos).Add(msg);
                    }
                    projection.Add((synonym is null ? "NULL::VARCHAR" :
                        headers.TryGetValue(synonym.Trim(), out var mappedColumn) ? mappedColumn : synonym.Trim()) +
                        " AS " + DuckSql.Id(col.Key));
                }
                var mapped = $"SELECT _arquivo,_membro,_aba,_linha,{string.Join(',',projection)} FROM grade WHERE {where} AND _linha>{header}";
                foreach (var expr in d.Derivadas)
                    mapped = d.Colunas.ContainsKey(expr.Key)
                        ? $"SELECT * REPLACE (({expr.Value}) AS {DuckSql.Id(expr.Key)}) FROM ({mapped})"
                        : $"SELECT *,({expr.Value}) AS {DuckSql.Id(expr.Key)} FROM ({mapped})";
                var output = string.Join(',', d.Saida.Select(s => $"cast({DuckSql.Id(s)} AS VARCHAR) AS {DuckSql.Id(s)}"));
                queries.Add($"SELECT {output},_aba AS _origem_aba,cast(_linha AS VARCHAR) AS _origem_linha,_membro AS _source_member FROM ({mapped}) WHERE {d.FiltroLinhas}");
                chosen.Add((g.Member,g.Sheet,where));
            }
            if (queries.Count == 0) report.Erros.Add("Nenhuma aba ou membro selecionado.");
            if (report.Erros.Count > 0) throw new InvalidDataException(JsonSerializer.Serialize(report));
            var union = string.Join(" UNION ALL ", queries);
            if (streaming)
            {
                WriteStreaming(c, d, arquivo, union, chosen, parquet, report, part, progress, ct);
                return;
            }
            DuckSql.Exec(c, "CREATE TABLE result AS " + union);
            foreach (var g in chosen)
            {
                var n = DuckSql.Count(c, $"SELECT count(*) FROM result WHERE _origem_aba IS NOT DISTINCT FROM {DuckSql.Lit(g.Sheet)} AND _source_member IS NOT DISTINCT FROM {DuckSql.Lit(g.Member)}");
                report.Abas.Add(new(arquivo,g.Member,g.Sheet,n));
                if (n < d.Validacoes.LinhasMinPorAba) report.Erros.Add($"Poucas linhas: {arquivo}/{g.Sheet}={n}");
            }
        }
        progress?.Invoke("Validando qualidade", 0, 0);
        var total = DuckSql.Count(c, "SELECT count(*) FROM result");
        if (total == 0) report.Erros.Add("Resultado vazio.");
        foreach (var name in d.Validacoes.Obrigatorias)
        {
            var n = DuckSql.Count(c, $"SELECT count(*) FROM result WHERE {DuckSql.Id(name)} IS NULL OR {DuckSql.Id(name)}=''");
            var pct = total == 0 ? 100 : 100.0*n/total;
            if (n > 0) (pct > d.Validacoes.NulosMaxPct.GetValueOrDefault(name) ? report.Erros : report.Avisos).Add($"{name}: {n}/{total} nulos ({pct.ToString("F4",CultureInfo.InvariantCulture)}%).");
        }
        if (d.Validacoes.ChaveUnica && DuckSql.Count(c,$"SELECT count(*) FROM (SELECT {string.Join(',',d.Chave.Select(DuckSql.Id))} FROM result GROUP BY ALL HAVING count(*)>1)") > 0)
            report.Erros.Add("Chave repetida.");
        if (d.Referencias.Count > 0)
        {
            foreach (var expr in d.Derivadas.Where(x => d.Referencias.Keys.Any(k => x.Value.Contains(k + "("))))
            {
                var n = DuckSql.Count(c,$"SELECT count(*) FROM result WHERE {DuckSql.Id(expr.Key)} IS NULL");
                if (n > 0) (100.0*n/Math.Max(1,total) > d.Validacoes.SemCorrespondenciaMaxPct ? report.Erros : report.Avisos).Add($"Referência ausente: {expr.Key} em {n} linhas.");
            }
        }
        part.Validations = report;
        if (report.Erros.Count > 0) throw new InvalidDataException(JsonSerializer.Serialize(report));
        var order = (derived ? string.Join(',',d.Chave.Length > 0 ? d.Chave.Select(DuckSql.Id) : d.Saida.Select(DuckSql.Id)) :
            "_origem_aba NULLS FIRST,_source_member NULLS FIRST,cast(_origem_linha AS BIGINT)") + ",rowid";
        var published = d.Saida.Concat(["_origem_aba","_origem_linha"]).ToArray();
        var select = string.Join(',',published.Select(DuckSql.Id));
        progress?.Invoke("Gravando Parquet", 0, 0);
        if (includeApiCompatibilityArtifacts)
        {
            DuckSql.Exec(c,$"COPY (SELECT cast(row_number() OVER (ORDER BY {order}) AS VARCHAR) AS _linha,{select} FROM result ORDER BY {order}) TO {DuckSql.Lit(parquet)} (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE 50000)");
        }
        else
        {
            // O carregador usa a ordem fisica do Parquet. Uma thread atribui
            // _linha durante a leitura sequencial, sem ordenar centenas de colunas.
            DuckSql.Exec(c,$"COPY (SELECT cast(row_number() OVER () AS VARCHAR) AS _linha,{select} FROM result) TO {DuckSql.Lit(parquet)} (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE 2048)");
            using var check = c.CreateCommand();
            check.CommandText = $"SELECT _linha FROM read_parquet({DuckSql.Lit(parquet)})";
            using var reader = check.ExecuteReader();
            long expected = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (!long.TryParse(reader.GetString(0), out var current) || current != ++expected)
                    throw new InvalidDataException($"Sequencia Parquet _linha invalida: esperado {expected}.");
            }
            if (expected != total)
                throw new InvalidDataException($"Contagem Parquet divergente: {expected}/{total}.");
        }
        ct.ThrowIfCancellationRequested();
        part.Rows = total;
        part.Columns = published.ToList();
        part.Segments = [];
        part.CoverageSha256 = "";
        if (includeApiCompatibilityArtifacts)
        {
            // The legacy HTTP host still needs its paged JSONL and row indexes.
            // The new Organizer CLI opts out and writes only the Parquet artifact.
            await using var sink = new Materializer.RowSink(folder, maxApiMaterializedBytes);
            sink.SetColumns(published);
            using var command = c.CreateCommand();
            command.CommandText = $"SELECT {select} FROM result ORDER BY {order}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
                await sink.WriteAsync(row, ct);
            }
            await sink.CompleteAsync(ct);
            if (sink.Rows != total)
                throw new InvalidDataException($"Contagem da materialização legada diverge do Parquet: {sink.Rows}/{total}.");
            part.Rows = sink.Rows;
            part.Columns = sink.Columns;
            part.Segments = sink.Segments;
            part.CoverageSha256 = sink.CoverageSha256;
        }
        part.SchemaHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(part.Columns))).ToLowerInvariant();
        part.ParquetFile="organized.parquet";
    }

    private static void WriteStreaming(DuckDBConnection c, ConjuntoDefinition d, string arquivo, string union,
        List<(string? Member, string? Sheet, string Where)> chosen, string parquet, QualityReport report,
        PublishedPartition part, Action<string, long, long>? progress, CancellationToken ct)
    {
        var published = d.Saida.Concat(["_origem_aba", "_origem_linha"]).ToArray();
        var select = string.Join(',', published.Select(DuckSql.Id));
        progress?.Invoke("Gravando Parquet", 0, 0);
        // Uma thread com ordem de insercao preservada: _linha segue a ordem fisica gravada.
        DuckSql.Exec(c, $"COPY (SELECT cast(row_number() OVER () AS VARCHAR) AS _linha,{select} FROM ({union})) " +
            $"TO {DuckSql.Lit(parquet)} (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE {RawGrid.RowGroupSize(published.Length + 1)})");
        ct.ThrowIfCancellationRequested();
        try
        {
            progress?.Invoke("Validando qualidade", 0, 0);
            var referenceColumns = d.Referencias.Count == 0 ? [] : d.Derivadas
                .Where(x => d.Referencias.Keys.Any(k => x.Value.Contains(k + "("))).Select(x => x.Key).ToArray();
            var metrics = new List<string>
            {
                "count(*)",
                "count(*) FILTER (WHERE TRY_CAST(_linha AS BIGINT) IS DISTINCT FROM file_row_number + 1)"
            };
            metrics.AddRange(d.Validacoes.Obrigatorias.Select(name =>
                $"count(*) FILTER (WHERE {DuckSql.Id(name)} IS NULL OR {DuckSql.Id(name)}='')"));
            metrics.AddRange(referenceColumns.Select(name => $"count(*) FILTER (WHERE {DuckSql.Id(name)} IS NULL)"));
            var values = new long[metrics.Count];
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"SELECT {string.Join(',', metrics)} FROM read_parquet({DuckSql.Lit(parquet)}, file_row_number=true)";
                using var r = cmd.ExecuteReader();
                r.Read();
                for (var i = 0; i < values.Length; i++) values[i] = Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
            }
            var total = values[0];
            if (values[1] > 0)
                throw new InvalidDataException($"Sequencia Parquet _linha invalida em {values[1]} linha(s).");

            var counts = new Dictionary<(string?, string?), long>();
            if (chosen.Count == 1) counts[(chosen[0].Member, chosen[0].Sheet)] = total;
            else
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT _source_member,_origem_aba,count(*) FROM ({union}) GROUP BY ALL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    counts[(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1))] =
                        Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
            }
            foreach (var g in chosen)
            {
                var n = counts.GetValueOrDefault((g.Member, g.Sheet));
                report.Abas.Add(new(arquivo, g.Member, g.Sheet, n));
                if (n < d.Validacoes.LinhasMinPorAba) report.Erros.Add($"Poucas linhas: {arquivo}/{g.Sheet}={n}");
            }
            if (total == 0) report.Erros.Add("Resultado vazio.");
            var index = 2;
            foreach (var name in d.Validacoes.Obrigatorias)
            {
                var n = values[index++];
                var pct = total == 0 ? 100 : 100.0 * n / total;
                if (n > 0) (pct > d.Validacoes.NulosMaxPct.GetValueOrDefault(name) ? report.Erros : report.Avisos)
                    .Add($"{name}: {n}/{total} nulos ({pct.ToString("F4", CultureInfo.InvariantCulture)}%).");
            }
            if (d.Validacoes.ChaveUnica && DuckSql.Count(c, $"SELECT count(*) FROM (SELECT {string.Join(',', d.Chave.Select(DuckSql.Id))} " +
                    $"FROM read_parquet({DuckSql.Lit(parquet)}) GROUP BY ALL HAVING count(*)>1)") > 0)
                report.Erros.Add("Chave repetida.");
            foreach (var name in referenceColumns)
            {
                var n = values[index++];
                if (n > 0) (100.0 * n / Math.Max(1, total) > d.Validacoes.SemCorrespondenciaMaxPct ? report.Erros : report.Avisos)
                    .Add($"Referência ausente: {name} em {n} linhas.");
            }
            part.Validations = report;
            if (report.Erros.Count > 0) throw new InvalidDataException(JsonSerializer.Serialize(report));
            part.Rows = total;
            part.Columns = published.ToList();
            part.Segments = [];
            part.CoverageSha256 = "";
            part.SchemaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(part.Columns))).ToLowerInvariant();
            part.ParquetFile = "organized.parquet";
        }
        catch
        {
            if (File.Exists(parquet)) File.Delete(parquet);
            throw;
        }
    }
}

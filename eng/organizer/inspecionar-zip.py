#!/usr/bin/env python3
"""Inspeciona membros e primeiras linhas dos ZIPs de uma função do catálogo v2.

Lê no máximo 64 KiB por membro para detectar texto e depois apenas as primeiras
linhas solicitadas. Não extrai o arquivo completo nem altera os downloads.
"""

import argparse
import csv
import fnmatch
import io
import json
import sys
import zipfile
from collections import Counter
from pathlib import Path


def read_json(path):
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def organizer_reads_member(name):
    leaf = name.rsplit("/", 1)[-1].lower()
    return leaf.endswith("csv") or leaf.rsplit(".", 1)[-1] in {
        "txt", "tsv", "xlsx", "xls", "dbf", "dbc"
    }


def sample_member(archive, member, max_rows):
    with archive.open(member) as stream:
        prefix = stream.read(65536)
    if not prefix:
        return "vazio", "", [], []
    if b"\x00" in prefix:
        return "binário", "", [], []
    try:
        prefix.decode("utf-8-sig")
        encoding = "utf-8-sig"
    except UnicodeDecodeError:
        encoding = "cp1252"
    preview = prefix.decode(encoding, errors="replace")
    try:
        delimiter = csv.Sniffer().sniff(preview, delimiters=";,\t|").delimiter
    except csv.Error:
        delimiter = max(";,\t|", key=lambda value: preview.count(value))
        if preview.count(delimiter) == 0:
            delimiter = ""
    if not delimiter:
        lines = preview.splitlines()[:max_rows]
        return encoding, "", [1] * len(lines), [[line] for line in lines]
    rows = []
    with archive.open(member) as raw:
        with io.TextIOWrapper(raw, encoding=encoding, errors="replace", newline="") as stream:
            reader = csv.reader(stream, delimiter=delimiter)
            for _, row in zip(range(max_rows), reader):
                rows.append(row)
    return encoding, delimiter, [len(row) for row in rows], rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--home", required=True, type=Path)
    parser.add_argument("--organization-id", required=True)
    parser.add_argument("--function-id", required=True)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--sample-source", action="append", default=[],
                        help="ID ou padrão glob de fonte para amostra completa; pode repetir")
    parser.add_argument("--max-rows", type=int, default=2)
    args = parser.parse_args()
    if args.max_rows not in range(1, 6):
        parser.error("--max-rows deve ser de 1 a 5")
    csv.field_size_limit(1024 * 1024)

    home = args.home.resolve(strict=True)
    catalog = read_json(home / "catalogo.json")
    receipts = read_json(home / "downloads-receipts.json")
    by_id = {item.get("arquivoId", item.get("ArquivoId")): item for item in receipts}
    agency = next((item for item in catalog["orgaos"]
                   if item["id"] == args.organization_id), None)
    if agency is None:
        parser.error("órgão ausente do catálogo")
    function = next((item for item in agency["funcoes"]
                     if item["id"] == args.function_id), None)
    if function is None:
        parser.error("função ausente do catálogo")

    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True, mode=0o700)
    report = output / "zip-inventario.tsv"
    samples = output / "zip-amostras.txt"
    counts = Counter()
    errors = []
    archives = 0
    members = 0
    with report.open("w", encoding="utf-8", newline="") as inventory, \
         samples.open("w", encoding="utf-8") as details:
        writer = csv.writer(inventory, delimiter="\t")
        writer.writerow(["source_id", "archive", "zip_bytes", "member", "member_bytes", "organizer_reads_member",
                         "encoding_sample", "delimiter", "field_counts", "first_values", "error"])
        for source in function["arquivos"]:
            if not source["nomeArquivo"].lower().endswith(".zip"):
                continue
            source_id = source["id"]
            receipt = by_id.get(source_id)
            if receipt is None:
                errors.append(f"{source_id}: recibo de download ausente")
                continue
            try:
                path = Path(receipt.get("destino", receipt.get("Destino", ""))).resolve(strict=True)
                if not path.is_relative_to(home):
                    raise ValueError("caminho do recibo fora de dados/")
                expected = receipt.get("tamanhoLocalBytes", receipt.get("TamanhoLocalBytes"))
                if not path.is_file() or path.stat().st_size != expected:
                    raise ValueError("arquivo ausente ou tamanho diferente do recibo")
                archives += 1
                with zipfile.ZipFile(path) as archive:
                    for member in archive.infolist():
                        if member.is_dir():
                            continue
                        members += 1
                        error = ""
                        try:
                            encoding, delimiter, widths, rows = sample_member(
                                archive, member, args.max_rows)
                        except (OSError, UnicodeError, csv.Error, ValueError, RuntimeError) as exc:
                            encoding, delimiter, widths, rows = "", "", [], []
                            error = str(exc)
                            errors.append(f"{source_id}/{member.filename}: {error}")
                        counts[(encoding, delimiter, tuple(widths))] += 1
                        writer.writerow([source_id, source["nomeArquivo"], path.stat().st_size,
                                         member.filename, member.file_size,
                                         "yes" if organizer_reads_member(member.filename) else "no", encoding,
                                         repr(delimiter), ",".join(map(str, widths)),
                                         json.dumps(rows[0][:8], ensure_ascii=False) if rows else "", error])
                        if any(fnmatch.fnmatchcase(source_id, pattern)
                               for pattern in args.sample_source):
                            details.write(f"\n=== {source_id} | {source['nomeArquivo']} | "
                                          f"{member.filename} | {member.file_size} bytes ===\n")
                            details.write(f"encoding={encoding}; delimiter={repr(delimiter)}; "
                                          f"fields={widths}\n")
                            for number, row in enumerate(rows, 1):
                                details.write(f"linha de amostra {number}:\n")
                                for column, value in enumerate(row, 1):
                                    details.write(f"  c{column}: {value[:120]!r}\n")

            except (OSError, ValueError, zipfile.BadZipFile, zipfile.LargeZipFile) as exc:
                errors.append(f"{source_id}: {exc}")
                writer.writerow([source_id, source["nomeArquivo"], "", "", "", "", "", "",
                                 "", "", str(exc)])

    print(f"ZIPs: {archives}; membros: {members}; erros: {len(errors)}")
    for (encoding, delimiter, widths), count in counts.most_common():
        print(f"  {count:3} membro(s): encoding amostral={encoding}, "
              f"delimitador={repr(delimiter)}, campos={','.join(map(str, widths))}")
    print(f"Inventário: {report}")
    print(f"Amostras selecionadas: {samples}")
    for error in errors[:10]:
        print("ERRO:", error, file=sys.stderr)
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())

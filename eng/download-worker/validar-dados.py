#!/usr/bin/env python3
"""Confere o estado de Downloads antes da troca de serviço, sem alterar dados."""

import json
import sys
from pathlib import Path


def fail(message: str) -> None:
    raise SystemExit(message)


def get(item: dict, key: str):
    return item.get(key, item.get(key[0].upper() + key[1:]))


def main() -> None:
    if len(sys.argv) != 2:
        fail("Uso: python3 validar-dados.py /caminho/govdata-manager-v2/dados")
    root = Path(sys.argv[1]).resolve(strict=True)
    documents = {}
    for name in ("catalogo.json", "downloads.json", "downloads-receipts.json", "settings.json"):
        with (root / name).open(encoding="utf-8") as stream:
            documents[name] = json.load(stream)
    catalog = documents["catalogo.json"]
    if get(catalog, "versao") != 2:
        fail("Catálogo não é v2.")
    sources = {}
    for agency in get(catalog, "orgaos") or []:
        for function in get(agency, "funcoes") or []:
            for source in get(function, "arquivos") or []:
                source_id = get(source, "id")
                if source_id in sources:
                    fail(f"ID duplicado no catálogo: {source_id}")
                sources[source_id] = source
    receipts = documents["downloads-receipts.json"]
    if not isinstance(receipts, list):
        fail("Recibos não são uma lista.")
    valid = set()
    problems = []
    stale = []
    download_root = get(documents["settings.json"], "downloadRoot")
    if download_root:
        try:
            Path(download_root).resolve(strict=True).relative_to(root)
        except (OSError, ValueError):
            problems.append("settings.json: downloadRoot ausente ou fora de dados/")
    for job in documents["downloads.json"]:
        destination = get(job, "destino")
        if destination:
            try:
                Path(destination).resolve(strict=False).relative_to(root)
            except ValueError:
                problems.append(f"job {get(job, 'id')}: destino fora de dados/")
    for receipt in receipts:
        source_id = get(receipt, "arquivoId")
        destination = get(receipt, "destino")
        source = sources.get(source_id)
        if not destination:
            problems.append(f"{source_id}: destino ausente")
            continue
        try:
            path = Path(destination).resolve(strict=True)
            path.relative_to(root)
            expected = get(receipt, "tamanhoLocalBytes")
            if not path.is_file() or path.stat().st_size != expected:
                raise ValueError("tamanho divergente")
            if source is None or get(receipt, "url") != get(source, "url"):
                stale.append(source_id)
            valid.add(source_id)
        except (OSError, ValueError) as exc:
            problems.append(f"{source_id}: {exc}")
    print(f"Catálogo v2: {len(sources)} fontes")
    print(f"Recibos: {len(receipts)}; arquivos com tamanho igual ao recibo dentro de dados/: {len(valid)}")
    print(f"Jobs persistidos: {len(documents['downloads.json'])}")
    print(f"Parquets preservados: {sum(1 for _ in (root / 'server/storage/releases').rglob('organized.parquet'))}")
    print(f"Recibos de fontes removidas ou alteradas: {len(stale)} (o worker reconciliará)")
    if problems:
        for problem in problems[:20]:
            print(f"DIVERGÊNCIA {problem}", file=sys.stderr)
        fail(f"Validação falhou: {len(problems)} divergência(s). Não inicie o novo serviço.")


if __name__ == "__main__":
    main()

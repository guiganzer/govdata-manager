#!/usr/bin/env python3
"""Inventário somente de leitura: catálogo v2, saídas READY e schema arquivo."""
import argparse
import json
import shlex
import subprocess
import sys
from collections import defaultdict
from pathlib import Path


def read_json(path):
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def planner(root, home, org, function):
    command = [str(root / "govdata-organizer"), "plan", "--home", str(home),
               "--organization-id", org, "--function-id", function]
    result = subprocess.run(command, text=True, capture_output=True, timeout=180)
    try:
        value = json.loads(result.stdout)
        if isinstance(value, dict) and value.get("command") == "plan":
            return value, None
    except json.JSONDecodeError:
        pass
    return None, (result.stderr.strip() or result.stdout.strip() or f"exit {result.returncode}")


def bronze_rows(database):
    sql = """SELECT p.publication_id, p.conjunto_id, p.partition_id, p.status,
                     p.rows_loaded, p.expected_rows
              FROM arquivo.particao p JOIN arquivo.publicacao pub USING (publication_id)
              ORDER BY p.publication_id, p.partition_id"""
    result = subprocess.run(["sudo", "-u", "postgres", "psql", "-X", "-v", "ON_ERROR_STOP=1",
                             "-d", database, "-At", "-F", "\t", "-c", sql],
                            text=True, capture_output=True, timeout=90)
    if result.returncode:
        raise RuntimeError("Consulta ao schema arquivo falhou: " + result.stderr.strip())
    rows = defaultdict(dict)
    for line in result.stdout.splitlines():
        values = line.split("\t")
        if len(values) != 6:
            raise RuntimeError("Resposta inesperada do PostgreSQL.")
        publication, conjunto, part, status, loaded, expected = values
        rows[publication][part] = (conjunto, status, int(loaded), int(expected))
    return rows


def run_inventory(roots):
    runs = []
    visited = set()
    for root in roots:
        if not root.is_dir():
            continue
        for path in root.rglob("organization-run.json"):
            resolved = path.resolve()
            if resolved in visited:
                continue
            visited.add(resolved)
            try:
                run = read_json(path)
                if run.get("schemaVersion") != 1 or run.get("status") != "READY":
                    continue
                if not (path.parent / "publications-v3.json").is_file():
                    continue
                runs.append((path.parent, run))
            except (OSError, ValueError, TypeError):
                continue
    return runs


def choose_run(runs, org_id, function_id, source_ids, document_ids):
    tabular = source_ids - document_ids
    candidates = []
    for directory, run in runs:
        if run.get("organizationId") != org_id:
            continue
        if run.get("functionId") not in (None, function_id):
            continue
        publications = run.get("publications") or []
        part_ids = {part.get("id") for pub in publications for part in pub.get("partitions", [])}
        if not tabular or not tabular.issubset(part_ids):
            continue
        relevant = [pub for pub in publications if any(
            part.get("id") in tabular for part in pub.get("partitions", []))]
        if relevant and all((directory / part["parquet"]).is_file()
                            for pub in relevant for part in pub.get("partitions", [])):
            candidates.append((str(run.get("completedAt", "")), directory, run, relevant))
    return max(candidates, default=None, key=lambda x: x[0])


def classify(plan, run, rows, source_ids, document_ids):
    if run is None:
        if plan is None:
            return "ERRO_PLANO", 0, 0, 0
        if plan.get("missingDownloads"):
            return "PENDENTE_DOWNLOAD", 0, 0, 0
        if plan.get("crossScopeDefinitions") or plan.get("unmapped") or not plan.get("ready"):
            return "SEM_LAYOUT_OU_REVISAO", 0, 0, 0
        return "PRONTO_PARA_ORGANIZAR", 0, 0, 0
    relevant = run[3]
    expected = [(pub["id"], part["id"], int(part["rows"]))
                for pub in relevant for part in pub.get("partitions", [])]
    matched = [rows.get(pub_id, {}).get(part_id) for pub_id, part_id, _ in expected]
    registered = sum(item is not None for item in matched)
    loaded = sum(item is not None and item[1] == "CARREGADA" and item[2] == item[3] == exp
                 for item, (_, _, exp) in zip(matched, expected))
    if any(item is not None and item[1] == "ERRO" for item in matched):
        status = "ERRO_CARGA"
    elif loaded == len(expected):
        status = "TRANSPORTADO"
    elif registered == 0:
        status = "ORGANIZADO_SEM_CADASTRO"
    elif loaded:
        status = "CARGA_PARCIAL"
    else:
        status = "CADASTRADO_PENDENTE_CARGA"
    if plan is None or not plan.get("ready"):
        status += "_REVISAR_FONTE"
    return status, registered, loaded, len(expected)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--home", type=Path, required=True)
    parser.add_argument("--database", default="dados_publicos")
    parser.add_argument("--run-root", type=Path, action="append", default=[])
    parser.add_argument("--organization-id")
    parser.add_argument("--function-id")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--commands", action="store_true", help="mostra próximo comando sugerido por função")
    args = parser.parse_args()
    home = args.home.resolve()
    root = Path(__file__).resolve().parents[2]
    catalog = read_json(home / "catalogo.json")
    if catalog.get("versao") != 2:
        raise RuntimeError("Esperado catálogo v2.")
    config = read_json(home / "organizer.json")
    output = Path(config.get("outputRoot", "organization-output"))
    if not output.is_absolute():
        output = home / output
    runs = run_inventory([output, *args.run_root])
    bronze = bronze_rows(args.database)
    report = []
    for org in catalog["orgaos"]:
        if args.organization_id and org["id"] != args.organization_id:
            continue
        for function in org["funcoes"]:
            if args.function_id and function["id"] != args.function_id:
                continue
            plan, error = planner(root, home, org["id"], function["id"])
            sources = {item["id"] for item in function["arquivos"]}
            documents = {item["id"] for item in function["arquivos"]
                         if item["nomeArquivo"].lower().endswith(".pdf")}
            run = choose_run(runs, org["id"], function["id"], sources, documents)
            status, registered, loaded, total = classify(plan, run, bronze, sources, documents)
            directory = str(run[1]) if run else ""
            report.append({
                "orgao": org["id"], "orgaoNome": org["nome"],
                "funcao": function["id"], "funcaoNome": function["nome"],
                "fontes": len(sources), "downloadsPendentes": len(plan.get("missingDownloads") or []) if plan else None,
                "semLayout": len(plan.get("unmapped") or []) if plan else None,
                "definicoes": plan.get("definitions") if plan else None,
                "publicacoesOrganizadas": len(run[3]) if run else 0,
                "particoesCadastradas": registered, "particoesCarregadas": loaded,
                "particoesOrganizadas": total, "status": status,
                "runDir": directory, "erro": error,
            })
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return
    print("ORGÃO\tNOME ÓRGÃO\tFUNÇÃO\tNOME FUNÇÃO\tFONTES\tDL PEND.\tSEM LAYOUT\tDEFINIÇÕES\tPUB.ORG\tPART.ORG\tCAD.\tBRONZE\tSTATUS\tRUN_DIR")
    for item in report:
        print("\t".join(str(item[key] if item[key] is not None else "-") for key in
                        ("orgao", "orgaoNome", "funcao", "funcaoNome", "fontes", "downloadsPendentes", "semLayout",
                         "definicoes", "publicacoesOrganizadas", "particoesOrganizadas", "particoesCadastradas",
                         "particoesCarregadas", "status", "runDir")))
        if args.commands:
            scope = f"--organization-id {shlex.quote(item['orgao'])} --function-id {shlex.quote(item['funcao'])}"
            command = None
            if item["status"] == "PRONTO_PARA_ORGANIZAR":
                command = f"./govdata-organizer organize {scope}"
            elif item["status"] in ("PENDENTE_DOWNLOAD", "SEM_LAYOUT_OU_REVISAO", "ERRO_PLANO"):
                command = f"./govdata-organizer plan {scope}"
            elif item["status"] == "ORGANIZADO_SEM_CADASTRO":
                command = ("sudo -u nifi /opt/govdata-manager/arquivo-loader/GovData.Downloads.ArquivoLoader "
                           "register-run --config /etc/govdata-manager/arquivo-loader.json "
                           f"--run-dir {shlex.quote(item['runDir'])}")
            elif item["status"] in ("CADASTRADO_PENDENTE_CARGA", "CARGA_PARCIAL"):
                command = ("sudo -u nifi /opt/govdata-manager/arquivo-loader/GovData.Downloads.ArquivoLoader "
                           "load --config /etc/govdata-manager/arquivo-loader.json "
                           f"--run-dir {shlex.quote(item['runDir'])} --max-partitions 1")
            if command:
                print("  PRÓXIMO: " + command)
        if item["erro"]:
            print(f"AVISO {item['orgao']}/{item['funcao']}: {item['erro']}", file=sys.stderr)
    print("\nTRANSPORTADO indica status/checkpoint no Bronze; use verify-data para comparar conteúdo integral.", file=sys.stderr)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as exc:
        print(f"ERRO: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc

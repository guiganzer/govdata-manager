#!/usr/bin/env python3
"""TUI própria do serviço de Downloads; não depende do Manager nem da API."""

from __future__ import annotations

import asyncio
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from textual import on
from textual.app import App, ComposeResult
from textual.binding import Binding
from textual.containers import Container, Horizontal
from textual.screen import ModalScreen
from textual.widgets import Button, DataTable, Footer, Header, Input, Static


PAGE_SIZE = 15
FILTERS = {
    "all": "TODOS", "baixando": "EM ANDAMENTO", "nafila": "NA FILA",
    "concluido": "CONCLUÍDOS", "falhou": "FALHAS", "pausado": "PAUSADOS",
}


def read_json(path: Path, fallback: Any) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return fallback


def field(obj: dict[str, Any], key: str, default: Any = "") -> Any:
    return obj.get(key, obj.get(key[0].upper() + key[1:], default))


class Confirm(ModalScreen[bool]):
    CSS = """
    Confirm { align: center middle; background: $background 80%; }
    #box { width: 74; height: auto; border: round #ff7783; background: #171a20; padding: 1 2; }
    #message { height: auto; margin-bottom: 1; }
    #buttons { height: 3; align-horizontal: right; }
    Button { margin-left: 1; }
    """

    def __init__(self, message: str):
        super().__init__()
        self._notice = message

    def compose(self) -> ComposeResult:
        with Container(id="box"):
            yield Static(self._notice, id="message")
            with Horizontal(id="buttons"):
                yield Button("Voltar", id="no")
                yield Button("Confirmar", id="yes", variant="error")

    @on(Button.Pressed)
    def choose(self, event: Button.Pressed) -> None:
        self.dismiss(event.button.id == "yes")


class Search(ModalScreen[str | None]):
    CSS = """
    Search { align: center middle; background: $background 80%; }
    #box { width: 76; height: 8; border: round #7fb5ff; background: #171a20; padding: 1 2; }
    #search { width: 1fr; }
    """

    def __init__(self, current: str):
        super().__init__()
        self._initial_text = current

    def compose(self) -> ComposeResult:
        with Container(id="box"):
            yield Static("Buscar por órgão, função, arquivo, ID ou erro")
            yield Input(value=self._initial_text, id="search")
            yield Static("Enter aplica · Esc cancela")

    def on_mount(self) -> None:
        self.query_one(Input).focus()

    @on(Input.Submitted)
    def submit(self, event: Input.Submitted) -> None:
        self.dismiss(event.value.strip())

    def key_escape(self) -> None:
        self.dismiss(None)


class DownloadsTui(App[None]):
    TITLE = "GovData Downloads"
    CSS = """
    Screen { background: #0f1117; color: #d9dee9; }
    #brand { color: #ff7783; text-style: bold; height: 2; padding: 0 1; }
    #nav { color: #8dbbff; height: 2; padding: 0 1; }
    #metrics { border: round #414a5a; height: 5; padding: 0 2; margin: 0 1 1 1; }
    #caption { color: #8dbbff; height: 2; padding: 0 1; }
    #rows { height: 1fr; margin: 0 1; }
    #message { color: #aeb8c7; height: 2; padding: 0 1; }
    #status { color: #9fdfbc; height: 2; padding: 0 1; }
    """
    BINDINGS = [
        Binding("f3", "filter_all", "Todos", priority=True),
        Binding("f4", "filter_downloading", "Em andamento", priority=True),
        Binding("f5", "filter_queue", "Fila", priority=True),
        Binding("f6", "filter_completed", "Concluídos", priority=True),
        Binding("f7", "filter_failed", "Falhas", priority=True),
        Binding("f8", "filter_paused", "Pausados", priority=True),
        Binding("f9,slash", "search", "Buscar", priority=True),
        Binding("f10", "check_now", "Verificar", priority=True),
        Binding("f11", "toggle_automation", "Pausar/retomar", priority=True),
        Binding("f12,q", "quit", "Sair", priority=True),
        Binding("pageup", "previous_page", "Anterior", show=False, priority=True),
        Binding("pagedown", "next_page", "Próxima", show=False, priority=True),
        Binding("p", "pause_job", "Pausar arquivo", priority=True),
        Binding("r", "resume_job", "Retomar arquivo", priority=True),
        Binding("enter", "details", "Detalhes", priority=True),
    ]

    def __init__(self, home: Path, worker: Path):
        super().__init__()
        self._home = home
        self._worker_path = worker
        self._filter_mode = "all"
        self._search_text = ""
        self._page_index = 0
        self._source_rows: list[dict[str, Any]] = []
        self._page_rows: list[dict[str, Any]] = []
        self._status_data: dict[str, Any] = {}
        self._notice = ""
        self._main_screen = None

    def compose(self) -> ComposeResult:
        yield Header(show_clock=False)
        yield Static("GOVDATA  /  DOWNLOADS AUTÔNOMO", id="brand")
        yield Static("F3 Todos  F4 Em andamento  F5 Fila  F6 Concluídos  F7 Falhas  F8 Pausados  F9 Buscar  F10 Verificar  F11 Automação", id="nav")
        yield Static("", id="metrics")
        yield Static("", id="caption")
        yield DataTable(id="rows", zebra_stripes=True, cursor_type="row")
        yield Static("", id="message")
        yield Static("", id="status")
        yield Footer()

    async def on_mount(self) -> None:
        self._main_screen = self.screen
        table = self.query_one(DataTable)
        for title, key in (("ÓRGÃO / FUNÇÃO", "scope"), ("ARQUIVO", "file"),
                           ("STATUS", "state"), ("BAIXADO", "bytes"), ("PRÓXIMA / ERRO", "detail")):
            table.add_column(title, key=key)
        await self.refresh_data()
        self.set_interval(5, self.refresh_data)

    async def refresh_data(self) -> None:
        catalog = read_json(self._home / "catalogo.json", {})
        jobs = read_json(self._home / "downloads.json", [])
        receipts = read_json(self._home / "downloads-receipts.json", [])
        self._status_data = read_json(self._home / "downloads-service.json", {})
        contexts: dict[str, tuple[str, str]] = {}
        sources: dict[str, dict[str, Any]] = {}
        total = 0
        for org in field(catalog, "orgaos", []) or []:
            org_name = str(field(org, "nome", field(org, "id")))
            for function in field(org, "funcoes", []) or []:
                func_name = str(field(function, "nome", field(function, "id")))
                for source in field(function, "arquivos", []) or []:
                    file_id = str(field(source, "id"))
                    contexts[file_id] = (org_name, func_name)
                    sources[file_id] = source
                    total += 1
        receipt_ids: set[str] = set()
        for item in receipts if isinstance(receipts, list) else []:
            if not isinstance(item, dict):
                continue
            file_id = str(field(item, "arquivoId"))
            source = sources.get(file_id)
            path = Path(str(field(item, "destino")))
            try:
                valid = (source is not None and field(item, "url") == field(source, "url")
                         and path.is_file()
                         and path.stat().st_size == int(field(item, "tamanhoLocalBytes", -1)))
            except (OSError, TypeError, ValueError):
                valid = False
            if valid:
                receipt_ids.add(file_id)
        self._source_rows = []
        latest_jobs: dict[str, dict[str, Any]] = {}
        for job in jobs if isinstance(jobs, list) else []:
            if not isinstance(job, dict):
                continue
            file_id = str(field(job, "arquivoId"))
            if file_id not in sources:
                continue
            previous = latest_jobs.get(file_id)
            if previous is None or str(field(job, "criadoEmUtc")) > str(field(previous, "criadoEmUtc")):
                latest_jobs[file_id] = job
        for file_id, source in sources.items():
            job = latest_jobs.get(file_id)
            org_name, func_name = contexts.get(file_id, ("?", "?"))
            state = str(field(job, "status", "Concluido" if file_id in receipt_ids else "Pendente")) if job else (
                "Concluido" if file_id in receipt_ids else "Pendente")
            if file_id in receipt_ids and state.casefold() in {"concluido", "obsoleto", "pendente"}:
                state = "Concluido"
            if state.casefold() == "concluido" and file_id not in receipt_ids:
                state = "Sem recibo"
            current = int(field(job, "bytesBaixados", 0) or 0) if job else 0
            total_bytes = field(job, "totalBytes", None) if job else None
            amount = f"{current:,}" if total_bytes is None else f"{current:,}/{int(total_bytes):,}"
            detail = str(field(job, "erro") or field(job, "proximaTentativaEmUtc") or "-")[:95] if job else "-"
            self._source_rows.append({"scope": f"{org_name} / {func_name}", "file": str(field(source, "nome") or file_id),
                              "fileId": file_id, "jobId": str(field(job, "id")) if job else "", "state": state,
                              "bytes": amount, "detail": detail,
                              "search": f"{org_name} {func_name} {file_id} {field(source, 'nome')} {detail}".casefold(),
                              "raw": {"source": source, "job": job}})
        self._source_rows.sort(key=lambda row: (row["scope"], row["fileId"]))
        if self.screen is self._main_screen:
            self._metrics(total, len(receipt_ids))
            self._render()

    def _metrics(self, total: int, receipts: int) -> None:
        snapshot = self._status_data.get("automation", {})
        updated = self._status_data.get("updatedAt", "")
        try:
            age = (datetime.now(timezone.utc) - datetime.fromisoformat(updated.replace("Z", "+00:00"))).total_seconds()
        except (ValueError, TypeError):
            age = float("inf")
        alive = bool(self._status_data.get("active")) and age < 15
        enabled = bool(snapshot.get("enabled")) and bool(self._status_data.get("profileEnabled", True))
        counts = self._status_data.get("jobs", {})
        self.query_one("#metrics", Static).update(
            f"[bold #7fb5ff]{receipts:,}/{total:,} fontes com recibo[/]  ·  "
            f"serviço: {'[green]ATIVO[/]' if alive else '[red]PARADO/SEM STATUS[/]'}  ·  "
            f"automação: {'[green]ATIVA[/]' if enabled else '[yellow]PAUSADA[/]'}\n"
            f"Baixando {counts.get('downloading', 0)}  ·  Fila {counts.get('queued', 0)}  ·  "
            f"Concluídos {counts.get('completed', 0)}  ·  Falhas {counts.get('failed', 0)}  ·  "
            f"Pausados {counts.get('paused', 0)}  ·  Próxima varredura {str(snapshot.get('nextScanUtc') or '-')[:19]}")
        self._notice = str(snapshot.get("lastError") or self._notice)

    def _render(self) -> None:
        if self.screen is not self._main_screen:
            return
        selected = self._filter_mode
        filtered = [row for row in self._source_rows
                    if (selected == "all" or row["state"].casefold() == selected)
                    and (not self._search_text or self._search_text.casefold() in row["search"])]
        pages = max(1, (len(filtered) + PAGE_SIZE - 1) // PAGE_SIZE)
        self._page_index = min(self._page_index, pages - 1)
        self._page_rows = filtered[self._page_index * PAGE_SIZE:(self._page_index + 1) * PAGE_SIZE]
        table = self.query_one(DataTable)
        table.clear(columns=False)
        for row in self._page_rows:
            table.add_row(row["scope"], row["file"], row["state"], row["bytes"], row["detail"])
        if self._page_rows:
            table.move_cursor(row=0, column=0, animate=False)
        self.query_one("#caption", Static).update(
            f"{FILTERS[self._filter_mode]}  ·  {len(filtered):,} fontes  ·  página {self._page_index + 1}/{pages}  ·  busca: {self._search_text or '-'}")
        self.query_one("#message", Static).update(self._notice[:180])
        self.query_one("#status", Static).update(
            "↑↓ selecionar  ·  PgUp/PgDn páginas  ·  Enter detalhes  ·  p pausar job  ·  r retomar job  ·  q sair  ·  atualização 5s")

    def _select(self, value: str) -> None:
        self._filter_mode = value
        self._page_index = 0
        self._render()

    def action_filter_all(self) -> None: self._select("all")
    def action_filter_downloading(self) -> None: self._select("baixando")
    def action_filter_queue(self) -> None: self._select("nafila")
    def action_filter_completed(self) -> None: self._select("concluido")
    def action_filter_failed(self) -> None: self._select("falhou")
    def action_filter_paused(self) -> None: self._select("pausado")

    def action_previous_page(self) -> None:
        self._page_index = max(0, self._page_index - 1)
        self._render()

    def action_next_page(self) -> None:
        self._page_index += 1
        self._render()

    def action_search(self) -> None:
        self.push_screen(Search(self._search_text), self._searched)

    def _searched(self, value: str | None) -> None:
        if value is not None:
            self._search_text = value
            self._page_index = 0
            self.call_later(self._render)

    def _selected_row(self) -> dict[str, Any] | None:
        if self.screen is not self._main_screen:
            return None
        table = self.query_one(DataTable)
        if not self._page_rows or table.cursor_row < 0:
            self.notify("Selecione um job.", severity="warning")
            return None
        return self._page_rows[min(table.cursor_row, len(self._page_rows) - 1)]

    def action_details(self) -> None:
        row = self._selected_row()
        if row:
            self.notify(json.dumps(row["raw"], ensure_ascii=False, indent=2)[:3000],
                        title="Detalhes do download", timeout=12)

    def action_pause_job(self) -> None:
        row = self._selected_row()
        if row and row["state"].casefold() in {"baixando", "nafila"}:
            self._confirm("pause-job", row["jobId"], f"Pausar {row['file']}?")
        elif row:
            self.notify("Este job não está baixando nem na fila.", severity="warning")

    def action_resume_job(self) -> None:
        row = self._selected_row()
        if row and row["state"].casefold() in {"pausado", "falhou"}:
            self._confirm("resume-job", row["jobId"], f"Retomar {row['file']} com prioridade?")
        elif row:
            self.notify("Este job não está pausado nem falhou.", severity="warning")

    def action_check_now(self) -> None:
        self._confirm("check-now", None, "Verificar agora o catálogo v2 e os recibos?")

    def action_toggle_automation(self) -> None:
        enabled = (bool(self._status_data.get("automation", {}).get("enabled"))
                   and bool(self._status_data.get("profileEnabled", True)))
        action = "pause-auto" if enabled else "resume-auto"
        self._confirm(action, None, "Pausar a automação de Downloads?" if enabled else "Retomar a automação de Downloads?")

    def _confirm(self, action: str, job_id: str | None, message: str) -> None:
        self.push_screen(Confirm(message), lambda accepted:
                         self.run_worker(self._send(action, job_id), exclusive=True) if accepted else None)

    async def _send(self, action: str, job_id: str | None) -> None:
        argv = [str(self._worker_path), "control", action, "--home", str(self._home)]
        if job_id:
            argv += ["--job-id", job_id]
        try:
            process = await asyncio.create_subprocess_exec(*argv,
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=10)
            if process.returncode:
                self.notify(stderr.decode(errors="replace")[:500], severity="error")
            else:
                self.notify(stdout.decode(errors="replace")[:500], title="Controle enviado")
        except (OSError, asyncio.TimeoutError) as exc:
            self.notify(str(exc), severity="error")


if __name__ == "__main__":
    home = Path(os.environ.get("GOVDATA_HOME", "")).expanduser()
    if not str(home) or not home.is_absolute():
        raise SystemExit("Defina GOVDATA_HOME absoluto antes de abrir a TUI.")
    worker = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / "dist/linux-x64/download-worker/GovData.Downloads.Worker.Cli"
    DownloadsTui(home, worker).run()

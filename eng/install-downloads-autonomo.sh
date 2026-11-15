#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
data_home="$project_root/dados"
worker="$project_root/dist/linux-x64/download-worker/GovData.Downloads.Worker.Cli"
runtime="$project_root/.runtime/downloads/venv"
unit_dir="$HOME/.config/systemd/user"
unit="$unit_dir/govdata-downloads.service"
[[ "${GOVDATA_HOME:-$data_home}" == "$data_home" ]] || {
  echo "GOVDATA_HOME deve ser $data_home" >&2; exit 2;
}
[[ -x "$worker" ]] || { echo "Worker não publicado: $worker" >&2; exit 2; }
[[ "$(findmnt -T "$data_home" -n -o TARGET)" == /mnt/dados ]] || {
  echo "Os dados não estão no filesystem /mnt/dados; instalação interrompida." >&2; exit 2;
}
for required in catalogo.json downloads.json downloads-receipts.json settings.json; do
  [[ -s "$data_home/$required" ]] || { echo "Dado obrigatório ausente: $data_home/$required" >&2; exit 2; }
done
for old_unit in govdata-manager.service govdata-downloads-v2.service govdata-downloads-autonomo.service; do
  if systemctl --user is-active --quiet "$old_unit"; then
    echo "Pare $old_unit antes de instalar o serviço autônomo." >&2; exit 2
  fi
done
if pgrep -af '[G]ovData.Downloads.Server.Cli|[G]ovData.Downloads.Worker.Cli|[m]anagerd.py'; then
  echo 'Processo anterior de Downloads/Manager ainda está ativo.' >&2; exit 2
fi
install -d -m 700 "$project_root/.runtime/downloads" "$data_home/downloads-control" "$unit_dir"
if [[ ! -x "$runtime/bin/python" ]]; then python3 -m venv "$runtime"; fi
"$runtime/bin/python" -m pip install --disable-pip-version-check \
  -r "$project_root/eng/download-worker/requirements.txt"
python3 - "$project_root/eng/systemd/govdata-downloads.service" "$unit" "$project_root" "$data_home" <<'PY'
import os, sys, tempfile
from pathlib import Path
source, target, root, home = map(Path, sys.argv[1:])
text = source.read_text(encoding='utf-8').replace('@MANAGER_ROOT@', str(root)).replace('@GOVDATA_HOME@', str(home))
fd, temp = tempfile.mkstemp(prefix='govdata-downloads-', dir=target.parent)
with os.fdopen(fd, 'w', encoding='utf-8') as stream:
    stream.write(text)
os.chmod(temp, 0o600)
os.replace(temp, target)
PY
chmod 755 "$project_root/govdata-downloads"
systemctl --user daemon-reload
systemctl --user enable --now govdata-downloads.service
systemctl --user status govdata-downloads.service --no-pager
printf 'TUI sob demanda: %s/govdata-downloads\n' "$project_root"

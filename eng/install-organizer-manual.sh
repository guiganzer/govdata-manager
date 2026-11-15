#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
home="$project_root/dados"
worker="$project_root/dist/linux-x64/manual-organizer/GovData.Downloads.Organizer.Cli"
[[ -x "$worker" ]] || { echo "Publique antes o organizador: $worker" >&2; exit 2; }
for file in catalogo.json downloads-receipts.json settings.json server/settings.json; do
  [[ -s "$home/$file" ]] || { echo "Arquivo obrigatório ausente: $home/$file" >&2; exit 2; }
done
[[ -d "$home/server/conjuntos" ]] || { echo 'Definições V3 ausentes.' >&2; exit 2; }
if [[ ! -e "$home/organizer.json" ]]; then
  install -m 600 "$project_root/eng/organizer/organizer.example.json" "$home/organizer.json"
fi
chmod 755 "$project_root/govdata-organizer"
printf 'Configuração: %s/organizer.json\n' "$home"
printf 'Plano: %s/govdata-organizer plan --organization-id ID\n' "$project_root"

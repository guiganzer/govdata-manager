#!/usr/bin/env bash
set -euo pipefail
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd -- "$here/../.." && pwd)"
dotnet_bin="${DOTNET_BIN:-$root/.dotnet/dotnet}"
if [[ ! -x "$dotnet_bin" ]]; then dotnet_bin="dotnet"; fi
if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo 'Uso: bash coletar.sh DIRETORIO_DO_LOTE [MAX_ARQUIVOS=3]' >&2
  exit 2
fi
"$dotnet_bin" "$here/bin/Release/net10.0/ParquetEvidence.dll" "$@"

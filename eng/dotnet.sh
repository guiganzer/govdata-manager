#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="$project_root/.dotnet/dotnet"
if [[ ! -x "$dotnet" ]]; then
  echo "SDK .NET local ausente. Execute eng/setup-dotnet-linux.sh." >&2
  exit 2
fi
export DOTNET_ROOT="$project_root/.dotnet"
export DOTNET_CLI_HOME="$project_root/.local/cli"
export NUGET_PACKAGES="$project_root/.local/nuget/packages"
export NUGET_HTTP_CACHE_PATH="$project_root/.local/nuget/http-cache"
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"
exec "$dotnet" "$@"

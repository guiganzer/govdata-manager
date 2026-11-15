#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="$project_root/.dotnet/dotnet"
[[ -x "$dotnet" ]] || { echo 'SDK local ausente; instale .NET 10 neste projeto antes de publicar.' >&2; exit 2; }
export DOTNET_ROOT="$project_root/.dotnet"
export DOTNET_CLI_HOME="$project_root/.local/cli"
export NUGET_PACKAGES="$project_root/.local/nuget/packages"
export NUGET_HTTP_CACHE_PATH="$project_root/.local/nuget/http-cache"
export DOTNET_MULTILEVEL_LOOKUP=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
export MSBUILDDISABLENODEREUSE=1
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"
project="$project_root/GovData.Downloads.Worker.Cli/GovData.Downloads.Worker.Cli.csproj"
output="$project_root/dist/linux-x64/download-worker"
"$dotnet" restore "$project" --runtime linux-x64 --disable-parallel --ignore-failed-sources \
  --configfile "$project_root/NuGet.Config"
"$dotnet" publish "$project" --configuration Release --runtime linux-x64 \
  --self-contained true --no-restore --output "$output" \
  --disable-build-servers '-m:1' '-p:BuildInParallel=false' '-p:UseSharedCompilation=false'
chmod 755 "$output/GovData.Downloads.Worker.Cli"
printf 'Downloads publicado em %s\n' "$output"

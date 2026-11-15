#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
sdk_version="$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$project_root/global.json")"
if [[ ! "$sdk_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][A-Za-z0-9.]+)?$ ]]; then
    printf '%s\n' 'Versao do SDK invalida em global.json.' >&2
    exit 1
fi

export DOTNET_CLI_HOME="$project_root/.local/dotnet-home"
export NUGET_PACKAGES="$project_root/.local/nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false

if [[ ! -d "$project_root/.dotnet/sdk/$sdk_version" ]]; then
    mkdir -p "$project_root/.local/bootstrap"
    installer="$project_root/.local/bootstrap/dotnet-install.sh"
    if command -v curl >/dev/null 2>&1; then
        curl --fail --location --silent --show-error https://dot.net/v1/dotnet-install.sh -o "$installer"
    elif command -v wget >/dev/null 2>&1; then
        wget -q https://dot.net/v1/dotnet-install.sh -O "$installer"
    else
        printf '%s\n' 'Instale curl ou wget para obter o instalador oficial.' >&2
        exit 1
    fi
    bash "$installer" --version "$sdk_version" --install-dir "$project_root/.dotnet" --no-path
fi

cd "$project_root"
actual_version="$(./.dotnet/dotnet --version)"
[[ "$actual_version" == "$sdk_version" ]] || { printf '%s\n' 'SDK local divergente.' >&2; exit 1; }
printf 'SDK local pronto: %s em %s/.dotnet\n' "$actual_version" "$project_root"
printf '%s\n' 'SDK e caches ficam em .dotnet/ e .local/, ambos ignorados pelo Git.'

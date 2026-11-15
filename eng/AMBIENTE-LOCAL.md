# Ambiente .NET local

O SDK deve ficar em `.dotnet/`, dentro deste projeto. A versao e determinada por `global.json`. Os scripts nao alteram o PATH persistente, o registro do Windows ou a instalacao global do .NET.

## Windows

Na raiz do projeto:

```powershell
./eng/setup-local.ps1
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.local/nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
./.dotnet/dotnet.exe build GovData.Downloads.Worker.Cli/GovData.Downloads.Worker.Cli.csproj
```

## Linux

```bash
bash eng/setup-dotnet-linux.sh
export DOTNET_CLI_HOME="$PWD/.local/dotnet-home"
export NUGET_PACKAGES="$PWD/.local/nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
./.dotnet/dotnet build GovData.Downloads.Worker.Cli/GovData.Downloads.Worker.Cli.csproj
```

O script instala o SDK adequado ao sistema em que e executado. Dependencias nativas do sistema operacional continuam sendo pre-requisitos; nao sao instaladas por estes scripts.

## Versionamento

Versionar `global.json`, `NuGet.Config` e os scripts em `eng/`. Nao versionar `.dotnet/`, `.local/`, `bin/`, `obj/` ou os dados operacionais. Essas pastas ja estao cobertas pelo `.gitignore` existente.

O cache NuGet e o estado da CLI sao locais quando as variaveis acima estao definidas. A instalacao usa os [scripts oficiais da Microsoft](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script). Instalar o ambiente nao inicia downloads de dados publicos nem acessa bancos de producao.

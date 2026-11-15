[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$configuration = Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = [string]$configuration.sdk.version
if ($sdkVersion -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.]+)?$') {
    throw 'Versao do SDK invalida em global.json.'
}
$installDirectory = Join-Path $projectRoot '.dotnet'
$dotnetExecutable = Join-Path $installDirectory 'dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.local/nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

if (-not (Test-Path -LiteralPath (Join-Path $installDirectory "sdk/$sdkVersion"))) {
    $bootstrapDirectory = Join-Path $projectRoot '.local/bootstrap'
    New-Item -ItemType Directory -Force -Path $bootstrapDirectory | Out-Null
    $installer = Join-Path $bootstrapDirectory 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    # O instalador pode chamar exit; execute-o em outro processo PowerShell.
    $powerShellExecutable = (Get-Process -Id $PID).Path
    & $powerShellExecutable -NoProfile -File $installer -Version $sdkVersion -InstallDir $installDirectory -NoPath
    if ($LASTEXITCODE -ne 0) { throw "Falha ao instalar o SDK $sdkVersion." }
}

Push-Location $projectRoot
try {
    $actualVersion = & $dotnetExecutable --version
    if ($LASTEXITCODE -ne 0 -or $actualVersion.Trim() -ne $sdkVersion) {
        throw "SDK local divergente: esperado $sdkVersion; obtido $actualVersion."
    }
    Write-Output "SDK local pronto: $actualVersion em $installDirectory"
    Write-Output 'SDK e caches ficam em .dotnet/ e .local/, ambos ignorados pelo Git.'
} finally {
    Pop-Location
}

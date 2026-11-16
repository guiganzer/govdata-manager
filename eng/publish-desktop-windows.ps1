[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.local/nuget/packages'
$sdk = Join-Path $projectRoot '.dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { & (Join-Path $PSScriptRoot 'setup-local.ps1') }
Push-Location $projectRoot
try {
    & $sdk workload install maui-windows --skip-manifest-update
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível preparar o MAUI Windows.' }
    & $sdk publish GovData.Downloads.Desktop/GovData.Downloads.Desktop.csproj -c Release -o dist/desktop-win-x64
    if ($LASTEXITCODE -ne 0) { throw 'A publicação do desktop falhou.' }
    Write-Output 'Pronto: dist/desktop-win-x64/GovData.Downloads.Desktop.exe'
    Write-Output 'Distribua a pasta inteira. O usuário final não precisa instalar o SDK .NET.'
} finally { Pop-Location }

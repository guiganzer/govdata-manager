param(
    [string]$Output = (Join-Path (Join-Path $PSScriptRoot '..') 'dist/govdata-downloads-src.zip')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
$stage = Join-Path (Join-Path $root 'dist') ('govdata-downloads-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'govdata-manager'
try {
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    $files = @(
        'global.json', 'NuGet.Config', 'govdata-downloads',
        'catalogos/origens-download.json',
        'docs/CONTRATO-CATALOGO.md', 'docs/catalogo-v2.schema.json',
        'eng/setup-dotnet-linux.sh', 'eng/publish-downloads-linux.sh',
        'eng/install-downloads-autonomo.sh', 'eng/criar-pacote-downloads.ps1',
        'eng/systemd/govdata-downloads.service',
        'eng/download-worker/dashboard.py', 'eng/download-worker/requirements.txt',
        'eng/download-worker/validar-dados.py', 'eng/download-worker/README.md'
    )
    foreach ($project in @('GovData.Downloads.Core', 'GovData.Downloads.Worker.Cli')) {
        $files += @(Get-ChildItem -LiteralPath (Join-Path $root $project) -File |
            Where-Object { $_.Extension -in @('.cs', '.csproj') } |
            ForEach-Object { "$project/$($_.Name)" })
    }
    foreach ($file in $files) {
        $source = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Arquivo obrigatório ausente: $source"
        }
        $target = Join-Path $package $file
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $outputPath) | Out-Null
    Compress-Archive -Path $package -DestinationPath $outputPath -CompressionLevel Optimal -Force
    Write-Host "Pacote de código: $outputPath"
    Write-Host "Arquivos: $($files.Count). Dados e artefatos antigos não incluídos."
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $resolvedStage = [System.IO.Path]::GetFullPath($stage)
        $allowedStageRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist')) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStage.StartsWith($allowedStageRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Stage fora de dist; limpeza recusada.' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

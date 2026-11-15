param(
    [string]$Output = (Join-Path (Join-Path $PSScriptRoot '..') 'dist/govdata-organizer-src.zip')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
$stage = Join-Path (Join-Path $root 'dist') ('govdata-organizer-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'govdata-manager'
try {
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    $files = @(
        'global.json', 'NuGet.Config', 'govdata-organizer',
        'docs/conjunto-v3.schema.json', 'docs/CONTRATO-CATALOGO.md',
        'docs/catalogo-v2.schema.json', 'docs/RFB-CNPJ-ORGANIZACAO.md',
        'eng/setup-dotnet-linux.sh', 'eng/publish-organizer-linux.sh',
        'eng/install-organizer-manual.sh', 'eng/criar-pacote-organizer.ps1',
        'eng/organizer/organizer.example.json', 'eng/organizer/README.md',
        'eng/organizer/inspecionar-zip.py'
    )
    foreach ($project in @('GovData.Downloads.Core', 'GovData.Downloads.Server',
                           'GovData.Downloads.Organizer.Cli')) {
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
    # Compress-Archive no Windows grava nomes com '\' no diretório central do ZIP.
    # O unzip do Ubuntu avisa sobre isso; gravar as entradas com '/' explicitamente.
    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Force
    }
    $zip = [System.IO.Compression.ZipFile]::Open(
        $outputPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $source = Join-Path $package $file
            $entry = 'govdata-manager/' + $file.Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $source, $entry,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $zip.Dispose()
    }
    $check = [System.IO.Compression.ZipFile]::OpenRead($outputPath)
    try {
        if ($check.Entries.Count -ne $files.Count) {
            throw "Quantidade de entradas divergente no ZIP: $($check.Entries.Count)/$($files.Count)"
        }
        foreach ($entry in $check.Entries) {
            if ($entry.FullName.Contains('\') -or
                -not $entry.FullName.StartsWith('govdata-manager/')) {
                throw "Caminho inválido no ZIP: $($entry.FullName)"
            }
        }
    }
    finally {
        $check.Dispose()
    }
    Write-Host "Pacote: $outputPath"
    Write-Host "Arquivos: $($files.Count). Dados e binários não incluídos."
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $resolvedStage = [System.IO.Path]::GetFullPath($stage)
        $allowedStageRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist')) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStage.StartsWith($allowedStageRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Stage fora de dist; limpeza recusada.' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

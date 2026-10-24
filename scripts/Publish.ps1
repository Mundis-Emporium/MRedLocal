$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot 'publish'
$output = Join-Path $publishRoot 'MRedLocal'
$project = Join-Path $repoRoot 'src/Mundi.Launcher/Mundi.Launcher.csproj'

if (Test-Path -LiteralPath $output) {
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    $expectedOutput = [IO.Path]::GetFullPath((Join-Path $publishRoot 'MRedLocal'))
    if ($resolvedOutput -ne $expectedOutput -or (Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The package output must be an ordinary folder inside publish.'
    }
    $runningLaunchers = Get-Process -Name MRedLocal -ErrorAction SilentlyContinue
    try {
        if ($runningLaunchers | Where-Object { $_.Path -eq (Join-Path $output 'MRedLocal.exe') }) {
            throw 'Close the packaged launcher before publishing.'
        }
    } finally { $runningLaunchers | ForEach-Object { $_.Dispose() } }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item -LiteralPath (Join-Path $repoRoot 'NOTICES.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $output -Force
@'
MRedLocal

Open MRedLocal.exe. Keep the adapters and notices folders beside it.
Use Installation to choose or download a separate RedM copy, then use Server setup
to prepare a supported local VORP or RSG server. Load your main character first.

Windows x64, RDR2, working RedM authentication and a local server are required.
The launcher checks supported framework files before making changes.
Keep it open until the second game closes so graphics settings can be restored.

Setup: https://github.com/Mundis-Emporium/MRedLocal#setup
Support: https://discord.gg/Nng77jKReK
Store: https://mundis.tebex.io

Sustainable Use License 1.0 permits personal and internal business use.
Sharing must be free and for noncommercial purposes. Keep copyright and license notices.
See LICENSE and NOTICES.md. Framework adapters keep their separate GPL licenses.
'@ | Set-Content -LiteralPath (Join-Path $output 'README.txt') -Encoding UTF8
$runtimeInfo = & dotnet msbuild $project -t:ResolveFrameworkReferences -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -getItem:ResolvedRuntimePack
if ($LASTEXITCODE -ne 0) { throw 'Could not locate runtime notices.' }
$runtimePacks = ($runtimeInfo | Out-String | ConvertFrom-Json).Items.ResolvedRuntimePack
$notices = Join-Path $output 'notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
foreach ($pack in $runtimePacks) {
    if ($pack.FrameworkName -notin @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) { continue }
    foreach ($name in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $source = Join-Path $pack.PackageDirectory $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $notices ($pack.FrameworkName + '-' + $name)) -Force
        }
    }
}
if ((Get-ChildItem -LiteralPath $notices -File).Count -lt 3) { throw 'Runtime notices are missing.' }
$archive = Join-Path $publishRoot 'MRedLocal-Windows-x64.zip'
Compress-Archive -LiteralPath $output -DestinationPath $archive -Force
Write-Host "Package: $archive"

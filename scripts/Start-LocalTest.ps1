$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishedExe = Join-Path $repoRoot 'publish/MRedLocal/MRedLocal.exe'

try {
    if (Test-Path -LiteralPath $publishedExe -PathType Leaf) {
        Start-Process -FilePath $publishedExe -WindowStyle Normal
        exit 0
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 8 SDK to build from source, or use the packaged launcher.'
    }
    $projectPath = Join-Path $repoRoot 'src/Mundi.Launcher/Mundi.Launcher.csproj'
    & dotnet build $projectPath -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
    $builtExe = Join-Path $repoRoot 'src/Mundi.Launcher/bin/Release/net8.0-windows/MRedLocal.exe'
    Start-Process -FilePath $builtExe -WindowStyle Normal
    exit 0
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}

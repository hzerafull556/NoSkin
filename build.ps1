param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "NoSkins.csproj"
$pluginOutDir = Join-Path $PSScriptRoot "Drop"

dotnet build $projectPath -c $Configuration -nologo

$builtDll = Join-Path $PSScriptRoot "bin\$Configuration\net48\NoSkins.dll"
if (-not (Test-Path $builtDll)) {
    throw "Compilacao finalizou sem gerar a DLL esperada: $builtDll"
}

if (-not (Test-Path $pluginOutDir)) {
    New-Item -ItemType Directory -Path $pluginOutDir | Out-Null
}

Copy-Item $builtDll (Join-Path $pluginOutDir "NoSkins.dll") -Force
Write-Host "Plugin gerado em: $pluginOutDir\\NoSkins.dll"

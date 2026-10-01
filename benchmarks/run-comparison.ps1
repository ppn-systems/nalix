param(
    [string]$OutDir = "",
    [string]$ResultsFile = "",
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $ScriptDir

if (-not $OutDir) {
    $OutDir = Join-Path $Root "build\comparison-results"
}

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
if ($env:LOCALAPPDATA) {
    $dotnetRoot = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
    if (Test-Path $dotnetRoot) {
        $env:DOTNET_ROOT = $dotnetRoot
        $env:PATH = "$dotnetRoot;$env:PATH"
    }
}

dotnet build (Join-Path $Root "benchmarks\Nalix.Comparison.Benchmarks\Nalix.Comparison.Benchmarks.csproj") -c Release -nologo -v q
dotnet build (Join-Path $Root "benchmarks\Nalix.Comparison.MagicOnion\Nalix.Comparison.MagicOnion.csproj") -c Release -nologo -v q

$Results = if ($ResultsFile) { $ResultsFile } else { Join-Path $OutDir "results.jsonl" }
if (Test-Path $Results) {
    Remove-Item $Results -Force
}
New-Item -ItemType File -Path $Results -Force | Out-Null

$Log = Join-Path $OutDir "console.log"
if (Test-Path $Log) {
    Remove-Item $Log -Force
}

$Bin = Join-Path $Root "build\bin\Release"
$CmpExe = Join-Path $Bin "Nalix.Comparison.Benchmarks\net10.0\Nalix.Comparison.Benchmarks.exe"
$MoExe = Join-Path $Bin "Nalix.Comparison.MagicOnion\net10.0\Nalix.Comparison.MagicOnion.exe"

$defaultLibs = @("raw-tcp", "kestrel-ws", "nalix-tcp", "nalix-ws", "nalix-tcp-aead", "signalr-ws-msgpack", "grpc-unary", "grpc-duplex", "grpc-unary-tls", "grpc-duplex-tls")
$defaultMoLibs = @("magiconion-unary", "magiconion-hub")

$libs = if ($env:LIBS) { $env:LIBS -split ',' } else { $defaultLibs }
$moLibs = if ($env:MO_LIBS) { $env:MO_LIBS -split ',' } else { $defaultMoLibs }

$benchArgs = @("--sizes", "32,1024", "--clients", "1,16,64", "--runs", "3", "--lat-warmup", "20000", "--lat-iters", "100000", "--warmup", "2", "--duration", "8", "--out", $Results)
if ($ExtraArgs) {
    $benchArgs += $ExtraArgs
}

foreach ($lib in $libs) {
    if (-not $lib) { continue }
    Write-Host "Running benchmark for $lib..."
    & $CmpExe bench $lib @benchArgs *>&1 | Tee-Object -FilePath $Log -Append
    Start-Sleep -Seconds 2
}

foreach ($lib in $moLibs) {
    if (-not $lib) { continue }
    Write-Host "Running benchmark for $lib..."
    & $MoExe bench $lib @benchArgs *>&1 | Tee-Object -FilePath $Log -Append
    Start-Sleep -Seconds 2
}

$TableOut = Join-Path $OutDir "tables.md"
python (Join-Path $Root "benchmarks\Nalix.Comparison.Benchmarks\aggregate.py") $Results | Set-Content -Path $TableOut
Write-Host "Tables written to $TableOut"

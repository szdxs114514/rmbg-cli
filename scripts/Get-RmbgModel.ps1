<#
.SYNOPSIS
    Downloads the RMBG-2.0 ONNX weights from ModelScope into models\onnx.

.DESCRIPTION
    ModelScope is used instead of Hugging Face because it is directly reachable
    from mainland China without a proxy.

    Backend selection:
      1. aria2c (multi-connection + resume) -- looked up in <repo>\tools\aria2
         (installed by "rmbg --install-aria2") and then on PATH;
      2. curl.exe (single connection, resumable).

    The script validates the result is a plausible ONNX file and prints its SHA256.

    NOTE: this file is intentionally ASCII-only so that Windows PowerShell 5.1
    (which decodes .ps1 files as ANSI unless a BOM is present) never garbles it.

.PARAMETER Variant
    Which exported precision to download:
      fp16      model_fp16.onnx      ~490 MB  default; best balance on GPU/NPU
      fp32      model.onnx           ~977 MB  reference export, safest on CPU
      int8      model_int8.onnx      ~349 MB  CPU friendly
      quantized model_quantized.onnx ~349 MB  alias of int8 export
      uint8     model_uint8.onnx     ~349 MB
      q4f16     model_q4f16.onnx     ~223 MB  smallest, lowest bandwidth
      q4        model_q4.onnx        ~350 MB
      bnb4      model_bnb4.onnx      ~339 MB

.PARAMETER OutputDirectory
    Target directory. Defaults to <repo>\models\onnx.

.PARAMETER Connections
    Parallel connections for aria2c (1-32). Default 16.

.PARAMETER NoAria2
    Skip aria2c and use curl.exe instead.

.PARAMETER Force
    Re-download even if the file already exists.

.EXAMPLE
    pwsh -File scripts\Get-RmbgModel.ps1
    pwsh -File scripts\Get-RmbgModel.ps1 -Variant fp32 -Connections 16 -Force
    pwsh -File scripts\Get-RmbgModel.ps1 -NoAria2
#>

[CmdletBinding()]
param(
    [ValidateSet('fp16', 'fp32', 'int8', 'quantized', 'uint8', 'q4f16', 'q4', 'bnb4')]
    [string] $Variant = 'fp16',

    [string] $OutputDirectory,

    [ValidateRange(1, 32)]
    [int] $Connections = 16,

    [switch] $NoAria2,

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$variantToFileName = @{
    'fp16'      = 'model_fp16.onnx'
    'fp32'      = 'model.onnx'
    'int8'      = 'model_int8.onnx'
    'quantized' = 'model_quantized.onnx'
    'uint8'     = 'model_uint8.onnx'
    'q4f16'     = 'model_q4f16.onnx'
    'q4'        = 'model_q4.onnx'
    'bnb4'      = 'model_bnb4.onnx'
}

$variantNotes = @{
    'fp16'      = 'Recommended default. Runs on DirectML/NPU; on CPU the tool auto-lowers graph optimization.'
    'fp32'      = 'Reference export. Largest and slowest to download, most conservative numerics.'
    'int8'      = 'Smallest practical footprint; best choice for CPU-only machines.'
    'quantized' = 'Same export as int8 (different file name).'
    'uint8'     = 'Unsigned 8-bit variant of the quantized export.'
    'q4f16'     = 'Smallest file, lowest bandwidth; some quality loss on fine edges.'
    'q4'        = '4-bit weights, roughly int8 sized.'
    'bnb4'      = 'bitsandbytes 4-bit export.'
}

$fileName = $variantToFileName[$Variant]
$baseUri = 'https://www.modelscope.cn/models/AI-ModelScope/RMBG-2.0/resolve/master/onnx'
$uri = "$baseUri/$fileName"

if (-not $OutputDirectory -or $OutputDirectory.Trim().Length -eq 0) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Split-Path -Parent $scriptRoot
    $OutputDirectory = Join-Path $repoRoot 'models\onnx'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$targetPath = Join-Path $OutputDirectory $fileName

Write-Host ''
Write-Host 'RMBG-2.0 ONNX weight downloader' -ForegroundColor Cyan
Write-Host "  Variant  : $Variant ($fileName)"
Write-Host "  Note     : $($variantNotes[$Variant])"
Write-Host "  Source   : $uri"
Write-Host "  Target   : $targetPath"
Write-Host ''

if ((Test-Path -LiteralPath $targetPath) -and -not $Force) {
    $existing = Get-Item -LiteralPath $targetPath
    Write-Host ("File already exists ({0:N1} MB). Use -Force to re-download." -f ($existing.Length / 1MB)) -ForegroundColor Yellow

    if ($existing.Length -lt 1MB) {
        Write-Host 'WARNING: the existing file is suspiciously small (probably a Git LFS pointer).' -ForegroundColor Yellow
        Write-Host 'Re-run this script with -Force to replace it.' -ForegroundColor Yellow
        exit 3
    }

    exit 0
}

$curl = Get-Command curl.exe -ErrorAction SilentlyContinue
if (-not $curl) {
    throw 'curl.exe was not found. It ships with Windows 10 1803 and later. Install curl or use a browser to download the file manually from the Source URL above.'
}

$partialPath = "$targetPath.partial"

# ---------------------------------------------------------------- backend pick
# Prefer aria2c (multi-connection + resume), then curl.exe, then a plain
# .NET WebClient stream. aria2c is looked up in the portable install directory
# first so that "rmbg --install-aria2" makes this script faster too.
$repoRootForTools = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$aria2Candidates = @(
    (Join-Path $repoRootForTools 'tools\aria2\aria2c.exe')
)

$aria2 = $aria2Candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $aria2) {
    $aria2Command = Get-Command aria2c.exe -ErrorAction SilentlyContinue
    if ($aria2Command) { $aria2 = $aria2Command.Source }
}

function Invoke-Aria2Download {
    param([string] $Executable, [string] $Uri, [string] $Directory, [string] $FileName, [int] $Split)

    Write-Host "Using aria2c ($Executable) with $Split connections." -ForegroundColor Cyan

    $aria2Args = @(
        "-x$Split", "-s$Split", '-k1M',
        '--continue=true',
        '--max-tries=5', '--retry-wait=3',
        '--timeout=30', '--connect-timeout=20',
        '--file-allocation=none',
        '--allow-overwrite=true', '--auto-file-renaming=false',
        '--console-log-level=warn', '--summary-interval=5',
        '-d', $Directory, '-o', $FileName, $Uri
    )

    & $Executable @aria2Args
    return $LASTEXITCODE
}

function Invoke-CurlDownload {
    param([string] $Uri, [string] $PartialPath)

    if (Test-Path -LiteralPath $PartialPath) {
        Write-Host 'Removing stale .partial file.' -ForegroundColor Yellow
        Remove-Item -LiteralPath $PartialPath -Force
    }

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) {
        throw 'Neither aria2c nor curl.exe is available. Install aria2 (winget install aria2.aria2) or download the file manually from the Source URL above.'
    }

    Write-Host 'Using curl.exe (single connection).' -ForegroundColor Cyan

    # --fail: turn HTTP errors into a non-zero exit code instead of writing an HTML error page.
    & curl.exe --fail --location --retry 5 --retry-delay 3 --connect-timeout 30 --progress-bar --output $PartialPath $Uri
    return $LASTEXITCODE
}

Write-Host 'Downloading (resumable, this may take a few minutes)...' -ForegroundColor Cyan

$useAria2 = -not $NoAria2 -and $aria2
if ($useAria2) {
    $exitCode = Invoke-Aria2Download -Executable $aria2 -Uri $uri -Directory $OutputDirectory -FileName $fileName -Split $Connections

    if (-not (Test-Path -LiteralPath $targetPath)) {
        throw "aria2c exited with $exitCode and the target file does not exist."
    }

    if ($exitCode -ne 0) {
        Write-Host "WARNING: aria2c exited with $exitCode; validating whatever was downloaded." -ForegroundColor Yellow
    }
}
else {
    $exitCode = Invoke-CurlDownload -Uri $uri -PartialPath $partialPath

    if ($exitCode -ne 0) {
        if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Force }
        throw "curl.exe failed with exit code $exitCode."
    }

    Move-Item -LiteralPath $partialPath -Destination $targetPath -Force
}

$downloaded = Get-Item -LiteralPath $targetPath
if ($downloaded.Length -lt 1MB) {
    Get-Content -LiteralPath $targetPath -TotalCount 3 | ForEach-Object { Write-Host "  | $_" }
    Remove-Item -LiteralPath $targetPath -Force
    throw 'Downloaded file is too small to be a real ONNX model; aborting.'
}

# ONNX is a protobuf; the first byte must be 0x08 (field 1, ir_version, varint).
$firstByte = [System.IO.File]::ReadAllBytes($targetPath)[0]
if ($firstByte -ne 0x08) {
    Write-Host ("WARNING: unexpected first byte 0x{0:X2}; the file may not be a valid ONNX model." -f $firstByte) -ForegroundColor Yellow
}

$final = Get-Item -LiteralPath $targetPath
$hash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash

Write-Host ''
Write-Host 'Download complete.' -ForegroundColor Green
Write-Host ("  Path   : {0}" -f $final.FullName)
Write-Host ("  Size   : {0:N1} MB" -f ($final.Length / 1MB))
Write-Host ("  SHA256 : {0}" -f $hash)
Write-Host ''
Write-Host 'Verify the pipeline with:' -ForegroundColor Cyan
Write-Host '  dotnet run --project src\RmbgCli -- --ep list'
Write-Host '  dotnet run --project src\RmbgCli -- -i photo.jpg -o out\photo_nobg.png'
Write-Host ''
Write-Host 'Tip: this same downloader is built into the CLI:' -ForegroundColor Cyan
Write-Host '  dotnet run --project src\RmbgCli -- --download-model fp16 --install-aria2'
Write-Host '  dotnet run --project src\RmbgCli -- --guide'
Write-Host ''

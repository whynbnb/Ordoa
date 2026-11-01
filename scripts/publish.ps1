[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet("framework", "single", "aot")]
    [string]$Mode = "framework",

    [string]$Rid = "",

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$Output = "",

    [switch]$Portable,

    [switch]$NoClean,

    [switch]$NoRestore,

    [switch]$Help
)

$ErrorActionPreference = "Stop"

function Show-Usage {
    @"
Publish Ordoa.

Usage:
  .\scripts\publish.ps1 [mode] [options]

Modes:
  framework   Framework-dependent, multi-file (requires .NET 10 runtime)
  single      Framework-dependent single-file (requires .NET 10 runtime)
  aot         NativeAOT, self-contained (requires a C/C++ toolchain)

Options:
  -Mode <mode>              framework, single or aot
  -Rid <RID>                Runtime identifier, e.g. win-x64, linux-x64, osx-arm64.
                            Current RID is detected when omitted.
  -Configuration <value>    Release or Debug (default: Release)
  -Output <directory>       Output directory
                            (default: artifacts\publish\<mode>\<RID|portable>)
  -Portable                 framework mode only: output without a RID
  -NoClean                  Keep existing files in the output directory
  -NoRestore                Skip dotnet restore
  -Help                     Show this help

Examples:
  .\scripts\publish.bat framework
  .\scripts\publish.bat single -Rid win-x64
  .\scripts\publish.bat aot
  .\scripts\publish.ps1 aot -Rid win-x64 -Configuration Release
"@
}

if ($Help) {
    Show-Usage
    exit 0
}

function Fail([string]$message) {
    throw $message
}

function Get-CurrentRid {
    $reported = ""
    $info = & dotnet --info 2>$null
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet --info failed."
    }
    foreach ($line in $info) {
        if ($line -match '^\s*RID:\s*(\S+)') {
            $reported = $Matches[1]
            break
        }
    }
    if ($reported -match '^(linux|osx|win)-') {
        return $reported
    }

    $onWindows = ($env:OS -eq "Windows_NT")
    if ($onWindows) {
        switch ($env:PROCESSOR_ARCHITECTURE) {
            "AMD64" { return "win-x64" }
            "ARM64" { return "win-arm64" }
            "x86"   { return "win-x86" }
        }
    }

    try {
        $osName = (& uname -s 2>$null)
        $arch = (& uname -m 2>$null)
        switch ("${osName}:$arch") {
            "Linux:x86_64"  { return "linux-x64" }
            "Linux:amd64"   { return "linux-x64" }
            "Linux:aarch64" { return "linux-arm64" }
            "Linux:arm64"   { return "linux-arm64" }
            "Linux:armv7l"  { return "linux-arm" }
            "Linux:armv8l"  { return "linux-arm" }
            "Darwin:x86_64" { return "osx-x64" }
            "Darwin:arm64"  { return "osx-arm64" }
            "Darwin:aarch64" { return "osx-arm64" }
        }
    } catch { /* fall through */ }

    Fail "Could not detect a supported RID (dotnet reported '$reported'); pass -Rid explicitly."
}

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path (Join-Path (Join-Path $Root "src") "Ordoa.App") "Ordoa.App.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "dotnet was not found. Install the .NET 10 SDK first."
}
if (-not (Test-Path -LiteralPath $Project)) {
    Fail "Project not found: $Project"
}

if ($Portable) {
    if ($Mode -ne "framework") {
        Fail "-Portable is only valid with framework mode."
    }
    if (-not [string]::IsNullOrWhiteSpace($Rid)) {
        Fail "-Portable and -Rid cannot be used together."
    }
}
elseif ([string]::IsNullOrWhiteSpace($Rid)) {
    $Rid = Get-CurrentRid
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $slug = if ([string]::IsNullOrWhiteSpace($Rid)) { "portable" } else { $Rid }
    $Output = Join-Path (Join-Path (Join-Path (Join-Path $Root "artifacts") "publish") $Mode) $slug
}

$Output = [System.IO.Path]::GetFullPath($Output)
$RootFull = [System.IO.Path]::GetFullPath($Root)
$RootOfOutput = [System.IO.Path]::GetPathRoot($Output)
if ($Output -eq $RootOfOutput -or $Output -eq $RootFull -or
    $Output -eq [Environment]::GetFolderPath("UserProfile")) {
    Fail "Refusing to clean dangerous output directory: $Output"
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
if (-not $NoClean) {
    Get-ChildItem -LiteralPath $Output -Force | Remove-Item -Recurse -Force
}

$onWindows = ($env:OS -eq "Windows_NT")
if ($Mode -eq "aot") {
    if ($onWindows) {
        if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
            Write-Warning "NativeAOT on Windows needs the Visual Studio C++ build tools and a Windows SDK."
        }
    }
    elseif (-not (Get-Command clang -ErrorAction SilentlyContinue) -and
            -not (Get-Command gcc -ErrorAction SilentlyContinue) -and
            -not (Get-Command cc -ErrorAction SilentlyContinue)) {
        Write-Warning "NativeAOT usually needs clang or gcc."
    }
}

$publishArgs = @(
    "publish", $Project,
    "-c", $Configuration,
    "-o", $Output,
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

if (-not [string]::IsNullOrWhiteSpace($Rid)) {
    $publishArgs += @("-r", $Rid)
}
if ($NoRestore) {
    $publishArgs += "--no-restore"
    if ($Mode -eq "aot") {
        Write-Warning "AOT with -NoRestore is only reliable after an AOT restore for $Rid."
    }
}

switch ($Mode) {
    "framework" {
        $publishArgs += @("--self-contained", "false", "-p:PublishSingleFile=false")
    }
    "single" {
        $publishArgs += @(
            "--self-contained", "false",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true"
        )
    }
    "aot" {
        $publishArgs += @(
            "--self-contained", "true",
            "-p:PublishAot=true",
            "-p:StripSymbols=true",
            "-p:TrimmerSingleWarn=true"
        )
    }
}

Write-Host "==> Ordoa publish"
Write-Host "    Mode:          $Mode"
Write-Host "    Configuration: $Configuration"
Write-Host "    RID:           $(if ($Rid) { $Rid } else { 'portable' })"
Write-Host "    Output:        $Output"
Write-Host ""

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed with exit code $LASTEXITCODE."
}

if ($Configuration -eq "Release" -or $Mode -eq "single") {
    # Debug symbols from managed/native packages are not needed at runtime.
    Get-ChildItem -LiteralPath $Output -File -Filter "*.pdb" -Recurse -Force | Remove-Item -Force
}

$files = @(Get-ChildItem -LiteralPath $Output -File -Recurse -Force)
$size = ($files | Measure-Object -Property Length -Sum).Sum
if ($null -eq $size) { $size = 0 }
$exeName = if ($Rid -like "win*" -or $onWindows) { "Ordoa.exe" } else { "Ordoa" }
$exe = Join-Path $Output $exeName

if ($Mode -eq "aot") {
    if (-not (Test-Path -LiteralPath $exe)) {
        Fail "The AOT publish did not produce a native executable. If you used -NoRestore, retry after an AOT restore."
    }
    if (Test-Path -LiteralPath (Join-Path $Output "Ordoa.dll")) {
        Fail "The output looks framework-dependent instead of AOT. Retry without -NoRestore."
    }
}

Write-Host ""
Write-Host "==> Publish complete"
Write-Host "    Output: $Output"
Write-Host ("    Files:  {0} file(s), {1:N1} MB" -f $files.Count, ($size / 1MB))
if (Test-Path -LiteralPath $exe) {
    Write-Host "    App:    $exe"
}
switch ($Mode) {
    "framework" { Write-Host "    Note:   The target machine needs the .NET 10 Desktop Runtime." }
    "single"    { Write-Host "    Note:   The target machine needs the .NET 10 Desktop Runtime; native libraries self-extract on first launch." }
    "aot"       { Write-Host "    Note:   Keep the SkiaSharp / HarfBuzz native libraries next to the AOT executable." }
}

exit 0

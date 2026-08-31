[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-FullyQualifiedPath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        $root = [IO.Path]::GetPathRoot($Path)
        return $root -match "^[A-Za-z]:[\\/]$" -or $root.StartsWith("\\")
    }
    catch {
        return $false
    }
}

if (-not (Test-FullyQualifiedPath $PackageDirectory)) {
    throw "PackageDirectory must be an absolute path."
}

$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "The package directory does not exist: $packageRoot"
}


$packageEntry = Get-Item -LiteralPath $packageRoot -Force
if (($packageEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The package directory cannot be a reparse point: $packageRoot"
}

$reparseEntries = @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -Force |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        }
)
if ($reparseEntries.Count -ne 0) {
    throw "Reparse entries are not allowed in the package: $($reparseEntries.Name -join ', ')"
}

$requiredFiles = @(
    "FileOrganizer.App.exe",
    "FileOrganizer.App.dll",
    "FileOrganizer.App.deps.json",
    "FileOrganizer.App.runtimeconfig.json",
    "FileOrganizer.Application.dll",
    "FileOrganizer.Core.dll",
    "FileOrganizer.Infrastructure.dll",
    "coreclr.dll",
    "hostfxr.dll"
)
foreach ($fileName in $requiredFiles) {
    $path = Join-Path $packageRoot $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The portable package is missing required file: $fileName"
    }

    if ((Get-Item -LiteralPath $path).Length -eq 0) {
        throw "The portable package contains an empty required file: $fileName"
    }
}

$files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse)
if ($files.Count -eq 0) {
    throw "The portable package is empty."
}

$packagePrefix = $packageRoot + [IO.Path]::DirectorySeparatorChar
foreach ($file in $files) {
    if (-not $file.FullName.StartsWith(
            $packagePrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "A published file escaped the package directory: $($file.FullName)"
    }
}

$pdbFiles = @($files | Where-Object Extension -EQ ".pdb")
if ($pdbFiles.Count -ne 0) {
    throw "Release symbols leaked into the portable package: $($pdbFiles.Name -join ', ')"
}

$forbiddenExtensions = @(
    ".cs",
    ".csproj",
    ".sln",
    ".slnx",
    ".pfx",
    ".snk",
    ".user",
    ".publishsettings"
)
$forbiddenFiles = @($files | Where-Object Extension -In $forbiddenExtensions)
if ($forbiddenFiles.Count -ne 0) {
    throw "Source or secret-bearing files leaked into the package: $($forbiddenFiles.Name -join ', ')"
}

$forbiddenDirectories = @(
    Get-ChildItem -LiteralPath $packageRoot -Directory -Recurse -Force |
        Where-Object Name -In @(".git", ".vs", "obj")
)
if ($forbiddenDirectories.Count -ne 0) {
    throw "Repository/build directories leaked into the package: $($forbiddenDirectories.Name -join ', ')"
}

$depsPath = Join-Path $packageRoot "FileOrganizer.App.deps.json"
$depsText = [IO.File]::ReadAllText($depsPath)
if ($depsText.IndexOf(
        "DiagnosticsSupport",
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Avalonia diagnostics support leaked into the Release dependency graph."
}

$deps = $depsText | ConvertFrom-Json
$runtimeTarget = [string] $deps.runtimeTarget.name
if ($runtimeTarget.IndexOf(
        "win-x64",
        [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "The dependency graph does not target win-x64: $runtimeTarget"
}

$runtimeConfigPath = Join-Path $packageRoot "FileOrganizer.App.runtimeconfig.json"
$runtimeConfig = [IO.File]::ReadAllText($runtimeConfigPath) | ConvertFrom-Json
$includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
$hasCoreFramework = $includedFrameworks |
    Where-Object name -EQ "Microsoft.NETCore.App"
if ($null -eq $hasCoreFramework) {
    throw "The runtime configuration does not describe a self-contained .NET package."
}

$diagnosticsProperty = $runtimeConfig.runtimeOptions.configProperties.PSObject.Properties[
    "Avalonia.Diagnostics.Diagnostic.IsEnabled"]
if ($null -eq $diagnosticsProperty -or $diagnosticsProperty.Value -ne $false) {
    throw "Avalonia diagnostics are not explicitly disabled in the Release runtime configuration."
}

$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
[pscustomobject] @{
    PackageDirectory = $packageRoot
    RuntimeTarget = $runtimeTarget
    FileCount = $files.Count
    TotalBytes = $totalBytes
    SelfContained = $true
    DiagnosticsEnabled = $false
}

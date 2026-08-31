[CmdletBinding()]
param(
    [string] $OutputDirectory = ""
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

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts\portable"))
$artifactPrefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $packageDirectory = Join-Path $artifactRoot "win-x64"
}
else {
    if (-not (Test-FullyQualifiedPath $OutputDirectory)) {
        throw "OutputDirectory must be an absolute path beneath $artifactRoot."
    }

    $packageDirectory = [IO.Path]::GetFullPath($OutputDirectory)
}

if (-not $packageDirectory.StartsWith(
        $artifactPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The portable package output must remain beneath $artifactRoot."
}

$currentPath = $packageDirectory
while ($null -ne $currentPath) {
    if (Test-Path -LiteralPath $currentPath) {
        $entry = Get-Item -LiteralPath $currentPath -Force
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The package output cannot traverse a reparse point: $currentPath"
        }
    }

    if ([string]::Equals(
            $currentPath,
            $repositoryRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        break
    }

    $parent = [IO.Directory]::GetParent($currentPath)
    $currentPath = if ($null -eq $parent) { $null } else { $parent.FullName }
}

if (Test-Path -LiteralPath $packageDirectory) {
    $existing = Get-ChildItem -LiteralPath $packageDirectory -Force
    if ($null -ne $existing) {
        throw "The package output already contains files. Choose a new empty directory: $packageDirectory"
    }
}

$project = Join-Path $repositoryRoot "src\FileOrganizer.App\FileOrganizer.App.csproj"
$restoreArguments = @(
    "restore",
    $project,
    "-r", "win-x64"
)
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) {
    throw "The win-x64 restore failed with exit code $LASTEXITCODE."
}

$publishArguments = @(
    "publish",
    $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "-p:ContinuousIntegrationBuild=true",
    "-o", $packageDirectory
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "The win-x64 publish failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot "Test-WindowsPortablePackage.ps1") `
    -PackageDirectory $packageDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Portable package verification failed with exit code $LASTEXITCODE."
}

Write-Host "Portable package ready: $packageDirectory"

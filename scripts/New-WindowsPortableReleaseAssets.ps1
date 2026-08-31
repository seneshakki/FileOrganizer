[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-Sha256Hex([string] $Path) {
    $stream = $null
    $algorithm = $null
    try {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $algorithm = [Security.Cryptography.SHA256]::Create()
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($stream)).Replace("-", "")
    }
    finally {
        if ($null -ne $algorithm) {
            $algorithm.Dispose()
        }
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Assert-NoReparseTraversal(
    [string] $Path,
    [string] $Boundary,
    [string] $Description) {
    $currentPath = [IO.Path]::GetFullPath($Path)
    $boundaryPath = [IO.Path]::GetFullPath($Boundary)
    while ($null -ne $currentPath) {
        if (Test-Path -LiteralPath $currentPath) {
            $entry = Get-Item -LiteralPath $currentPath -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description cannot traverse a reparse point: $currentPath"
            }
        }

        if ([string]::Equals(
                $currentPath,
                $boundaryPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        $parent = [IO.Directory]::GetParent($currentPath)
        $currentPath = if ($null -eq $parent) { $null } else { $parent.FullName }
    }

    throw "$Description escaped its repository boundary: $Path"
}

if ($Version -notmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:(?:0|[1-9][0-9]*)|(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))(?:\.(?:(?:0|[1-9][0-9]*)|(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)))*)?$') {
    throw "Version must be a canonical semantic version without build metadata."
}
if ($ExpectedCommit -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "ExpectedCommit must be a full 40-character Git commit SHA."
}
$ExpectedCommit = $ExpectedCommit.ToLowerInvariant()

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$portableRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts\portable"))
$portablePrefix = $portableRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$packageParent = [IO.Directory]::GetParent($packageRoot)
if ($null -eq $packageParent -or
    -not [string]::Equals(
        $packageParent.FullName,
        $portableRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not $packageRoot.StartsWith(
        $portablePrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "PackageDirectory must be an existing direct child of $portableRoot."
}
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "PackageDirectory does not exist: $packageRoot"
}
Assert-NoReparseTraversal `
    -Path $packageRoot `
    -Boundary $repositoryRoot `
    -Description "PackageDirectory"

$verifier = Join-Path $PSScriptRoot "Test-WindowsPortablePackage.ps1"
& $verifier -PackageDirectory $packageRoot -ExpectedCommit $ExpectedCommit

$entryExecutable = Join-Path $packageRoot "FileOrganizer.App.exe"
$productVersion = (Get-Item -LiteralPath $entryExecutable).VersionInfo.ProductVersion
$expectedProductVersion = "$Version+$ExpectedCommit"
if (-not [string]::Equals(
        $productVersion,
        $expectedProductVersion,
        [StringComparison]::Ordinal)) {
    throw "The package ProductVersion does not match $expectedProductVersion`: $productVersion"
}

$releaseRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts\release"))
$payloadName = "FileOrganizer-$Version-win-x64-portable"
$bundleName = "$payloadName-assets"
$archiveName = "$payloadName.zip"
$checksumName = "$archiveName.sha256"
$bundlePath = Join-Path $releaseRoot $bundleName

Assert-NoReparseTraversal `
    -Path $releaseRoot `
    -Boundary $repositoryRoot `
    -Description "Release artifact path"
$null = [IO.Directory]::CreateDirectory($releaseRoot)
Assert-NoReparseTraversal `
    -Path $releaseRoot `
    -Boundary $repositoryRoot `
    -Description "Release artifact path"
if (Test-Path -LiteralPath $bundlePath) {
    throw "The release asset bundle already exists: $bundlePath"
}

$stagingRoot = Join-Path `
    $releaseRoot `
    (".FileOrganizer-release-assets-" + [Guid]::NewGuid().ToString("N"))
$stagingPayload = Join-Path $stagingRoot $payloadName
$stagingBundle = Join-Path $stagingRoot $bundleName
$stagingArchive = Join-Path $stagingBundle $archiveName
$stagingChecksum = Join-Path $stagingBundle $checksumName
$verificationRoot = Join-Path $stagingRoot "verified-extraction"
try {
    $null = [IO.Directory]::CreateDirectory($stagingPayload)
    $null = [IO.Directory]::CreateDirectory($stagingBundle)
    Assert-NoReparseTraversal `
        -Path $stagingPayload `
        -Boundary $releaseRoot `
        -Description "Release payload staging path"
    Assert-NoReparseTraversal `
        -Path $stagingBundle `
        -Boundary $releaseRoot `
        -Description "Release bundle staging path"
    Assert-NoReparseTraversal `
        -Path $packageRoot `
        -Boundary $repositoryRoot `
        -Description "PackageDirectory"
    $packagePrefix = $packageRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    foreach ($directory in [IO.Directory]::EnumerateDirectories(
            $packageRoot,
            "*",
            [IO.SearchOption]::AllDirectories)) {
        $relativeDirectory = $directory.Substring($packagePrefix.Length)
        $null = [IO.Directory]::CreateDirectory(
            (Join-Path $stagingPayload $relativeDirectory))
    }
    foreach ($file in [IO.Directory]::EnumerateFiles(
            $packageRoot,
            "*",
            [IO.SearchOption]::AllDirectories)) {
        $relativeFile = $file.Substring($packagePrefix.Length)
        [IO.File]::Copy(
            $file,
            (Join-Path $stagingPayload $relativeFile),
            $false)
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stagingPayload,
        $stagingArchive,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)
    Expand-Archive -LiteralPath $stagingArchive -DestinationPath $verificationRoot
    $topLevelEntries = @(Get-ChildItem -LiteralPath $verificationRoot -Force)
    if ($topLevelEntries.Count -ne 1 -or
        -not $topLevelEntries[0].PSIsContainer -or
        -not [string]::Equals(
            $topLevelEntries[0].Name,
            $payloadName,
            [StringComparison]::Ordinal)) {
        throw "The release archive does not contain exactly the expected top-level directory."
    }

    & $verifier `
        -PackageDirectory $topLevelEntries[0].FullName `
        -ExpectedCommit $ExpectedCommit

    $archiveHash = Get-Sha256Hex $stagingArchive
    $checksumText = "$archiveHash  $archiveName`n"
    [IO.File]::WriteAllText(
        $stagingChecksum,
        $checksumText,
        [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $bundlePath) {
        throw "The release asset bundle appeared before publication: $bundlePath"
    }
    Assert-NoReparseTraversal `
        -Path $releaseRoot `
        -Boundary $repositoryRoot `
        -Description "Release artifact path"
    Assert-NoReparseTraversal `
        -Path $stagingBundle `
        -Boundary $releaseRoot `
        -Description "Release bundle staging path"
    try {
        [IO.Directory]::Move($stagingBundle, $bundlePath)
    }
    catch {
        throw "The verified release assets could not be published without replacing an existing bundle: $($_.Exception.Message)"
    }

    [pscustomobject] @{
        Version = $Version
        Commit = $ExpectedCommit
        Archive = Join-Path $bundlePath $archiveName
        Checksum = Join-Path $bundlePath $checksumName
        Sha256 = $archiveHash
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        $cleanupSafe = $true
        try {
            Assert-NoReparseTraversal `
                -Path $releaseRoot `
                -Boundary $repositoryRoot `
                -Description "Release cleanup path"
            Assert-NoReparseTraversal `
                -Path $stagingRoot `
                -Boundary $releaseRoot `
                -Description "Release staging cleanup path"
        }
        catch {
            $cleanupSafe = $false
            Write-Warning "Refusing release staging cleanup after path validation failed: $($_.Exception.Message)"
        }

        if ($cleanupSafe) {
            $stagingEntry = Get-Item -LiteralPath $stagingRoot -Force
            if (($stagingEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                [IO.Directory]::GetParent($stagingEntry.FullName).FullName -ne $releaseRoot -or
                $stagingEntry.Name -notmatch '^\.FileOrganizer-release-assets-[0-9a-f]{32}$') {
                Write-Warning "Refusing to clean an unexpected release-asset staging path: $($stagingEntry.FullName)"
            }
            else {
                [IO.Directory]::Delete($stagingEntry.FullName, $true)
            }
        }
    }
}

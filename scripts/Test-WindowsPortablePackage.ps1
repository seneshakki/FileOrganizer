[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-FullyQualifiedPath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        [void] [IO.Path]::GetFullPath($Path)
        $normalizedPath = $Path.Replace(
            [IO.Path]::AltDirectorySeparatorChar,
            [IO.Path]::DirectorySeparatorChar)
        $isDriveRooted = $normalizedPath -match '^[A-Za-z]:\\'
        $isCompleteUnc = $normalizedPath -match `
            '^\\\\(?![?.]\\)[^\\]+\\[^\\]+(?:\\|$)'
        return $isDriveRooted -or $isCompleteUnc
    }
    catch {
        return $false
    }
}

function Get-PublishedDependencyAssetPath(
    [string] $AssetName,
    [string] $AssetGroup,
    [object] $Metadata) {
    $normalizedAssetName = $AssetName.Replace(
        [IO.Path]::AltDirectorySeparatorChar,
        [IO.Path]::DirectorySeparatorChar)
    $assetSegments = @($normalizedAssetName -split '\\')
    if ([IO.Path]::IsPathRooted($normalizedAssetName) -or
        $assetSegments.Count -eq 0 -or
        @($assetSegments | Where-Object {
                [string]::IsNullOrWhiteSpace($_) -or
                $_ -in @(".", "..") -or
                $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
            }).Count -ne 0) {
        throw "The dependency graph contains an invalid $AssetGroup asset path: $AssetName"
    }

    $leafName = [IO.Path]::GetFileName($normalizedAssetName)
    if ([string]::IsNullOrWhiteSpace($leafName)) {
        throw "The dependency graph contains an invalid $AssetGroup asset: $AssetName"
    }

    $usesLocale = $AssetGroup -eq "resources"
    if ($AssetGroup -notin @("resources", "runtimeTargets")) {
        return $leafName
    }

    if ($AssetGroup -eq "runtimeTargets") {
        $assetTypeProperty = $Metadata.PSObject.Properties["assetType"]
        if ($null -eq $assetTypeProperty) {
            return $leafName
        }

        $assetType = [string] $assetTypeProperty.Value
        if ($assetType -notin @("runtime", "native", "resources", "resource")) {
            throw "The dependency graph contains an unsupported runtimeTargets asset type: $assetType"
        }

        $usesLocale = $assetType -in @("resources", "resource")
        if (-not $usesLocale) {
            return $leafName
        }
    }

    $localeProperty = $Metadata.PSObject.Properties["locale"]
    $locale = if ($null -eq $localeProperty) {
        ""
    }
    else {
        [string] $localeProperty.Value
    }

    if ([string]::IsNullOrWhiteSpace($locale) -or
        $locale.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $locale.Contains("\\") -or
        $locale.Contains("/") -or
        $locale -in @(".", "..")) {
        throw "The dependency graph contains an invalid resource locale: $locale"
    }

    return Join-Path $locale $leafName
}

function Get-PortableExecutableMachine([string] $Path) {
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        if ($stream.Length -lt 64) {
            throw "The file is too small to contain a PE header."
        }

        $reader = New-Object IO.BinaryReader($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) {
            throw "The DOS header signature is missing."
        }

        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
            throw "The PE header offset is outside the file."
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "The PE header signature is missing."
        }

        return $reader.ReadUInt16()
    }
    catch {
        throw "The package entry executable is not a valid PE image: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        elseif ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

if (-not (Test-FullyQualifiedPath $PackageDirectory)) {
    throw "PackageDirectory must be an absolute path."
}

if ($ExpectedCommit -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "ExpectedCommit must be a full 40-character Git commit SHA."
}
$ExpectedCommit = $ExpectedCommit.ToLowerInvariant()

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
    "clrjit.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "System.Private.CoreLib.dll",
    "System.Runtime.dll"
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

$entryExecutable = Join-Path $packageRoot "FileOrganizer.App.exe"
$entryMachine = Get-PortableExecutableMachine $entryExecutable
if ($entryMachine -ne 0x8664) {
    throw ("FileOrganizer.App.exe does not target x64 (PE machine 0x{0:X4})." -f `
            $entryMachine)
}

$files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force)
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

$firstPartyBinaries = @(
    $files |
        Where-Object {
            $_.Name -like "FileOrganizer*.dll" -or
            $_.Name -like "FileOrganizer*.exe"
        } |
        Sort-Object FullName)
foreach ($firstPartyBinary in $firstPartyBinaries) {
    $relativePath = $firstPartyBinary.FullName.Substring($packagePrefix.Length)
    $productVersion = $firstPartyBinary.VersionInfo.ProductVersion
    $revisionMatch = [Text.RegularExpressions.Regex]::Match(
        [string] $productVersion,
        '\+(?<revision>[0-9A-Fa-f]{40})$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $revisionMatch.Success -or
        -not [string]::Equals(
            $revisionMatch.Groups["revision"].Value,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$relativePath does not identify exact expected SourceRevisionId $ExpectedCommit in ProductVersion: $productVersion"
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
    ".key",
    ".p12",
    ".pem",
    ".pfx",
    ".pubxml",
    ".snk",
    ".user",
    ".publishsettings"
)
$forbiddenFiles = @(
    $files |
        Where-Object {
            $_.Extension -in $forbiddenExtensions -or
            $_.Name -eq ".env" -or
            $_.Name.StartsWith(
                ".env.",
                [StringComparison]::OrdinalIgnoreCase) -or
            ($_.Extension -eq ".json" -and
                $_.Name -match '(?i)(credential|secret).*\.json$')
        })
if ($forbiddenFiles.Count -ne 0) {
    $forbiddenRelativePaths = @(
        $forbiddenFiles |
            ForEach-Object { $_.FullName.Substring($packagePrefix.Length) } |
            Sort-Object -Unique)
    throw "Source or secret-bearing files leaked into the package: $($forbiddenRelativePaths -join ', ')"
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
$runtimeTargetSeparator = $runtimeTarget.LastIndexOf("/")
if ($runtimeTargetSeparator -lt 0) {
    throw "The dependency graph does not target win-x64: $runtimeTarget"
}
$runtimeIdentifier = $runtimeTarget.Substring($runtimeTargetSeparator + 1)
if (-not [string]::Equals(
        $runtimeIdentifier,
        "win-x64",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The dependency graph does not target win-x64: $runtimeTarget"
}

$targetsProperty = $deps.PSObject.Properties["targets"]
if ($null -eq $targetsProperty) {
    throw "The dependency graph does not contain any publish targets."
}

$selectedTargetProperty = $targetsProperty.Value.PSObject.Properties[$runtimeTarget]
if ($null -eq $selectedTargetProperty) {
    throw "The dependency graph does not contain its selected target: $runtimeTarget"
}

$dependencyAssetPaths = @()
foreach ($libraryProperty in @($selectedTargetProperty.Value.PSObject.Properties)) {
    foreach ($assetGroup in @("runtime", "native", "resources")) {
        $assetGroupProperty = $libraryProperty.Value.PSObject.Properties[$assetGroup]
        if ($null -eq $assetGroupProperty) {
            continue
        }

        foreach ($assetProperty in @($assetGroupProperty.Value.PSObject.Properties)) {
            $dependencyAssetPaths += Get-PublishedDependencyAssetPath `
                -AssetName $assetProperty.Name `
                -AssetGroup $assetGroup `
                -Metadata $assetProperty.Value
        }
    }

    $runtimeTargetsProperty = $libraryProperty.Value.PSObject.Properties[
        "runtimeTargets"]
    if ($null -eq $runtimeTargetsProperty) {
        continue
    }

    foreach ($assetProperty in @($runtimeTargetsProperty.Value.PSObject.Properties)) {
        $dependencyAssetPaths += Get-PublishedDependencyAssetPath `
            -AssetName $assetProperty.Name `
            -AssetGroup "runtimeTargets" `
            -Metadata $assetProperty.Value
    }
}

$dependencyAssetPaths = @(
    $dependencyAssetPaths |
        Where-Object {
            [IO.Path]::GetExtension($_) -ne ".pdb" -and
            [IO.Path]::GetFileName($_) -ne "_._"
        } |
        Sort-Object -Unique)
if ($dependencyAssetPaths.Count -eq 0) {
    throw "The selected dependency target does not declare publish assets: $runtimeTarget"
}

foreach ($assetRelativePath in $dependencyAssetPaths) {
    $assetPath = Join-Path $packageRoot $assetRelativePath
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "The package is missing a dependency asset declared by $runtimeTarget`: $assetRelativePath"
    }

    if ((Get-Item -LiteralPath $assetPath).Length -eq 0) {
        throw "The package contains an empty dependency asset declared by $runtimeTarget`: $assetRelativePath"
    }
}

# The dependency graph is the deterministic publish inventory for runtime and
# native payloads. The apphost is generated by the SDK and is not a deps asset.
$expectedPayloadPaths = @(
    $dependencyAssetPaths + "FileOrganizer.App.exe" |
        Sort-Object -Unique)
$executableOrAssemblyExtensions = @(
    ".appx",
    ".bat",
    ".cmd",
    ".com",
    ".cpl",
    ".dll",
    ".dylib",
    ".exe",
    ".hta",
    ".jar",
    ".js",
    ".jse",
    ".msi",
    ".msix",
    ".msp",
    ".netmodule",
    ".ocx",
    ".ps1",
    ".psd1",
    ".psm1",
    ".scr",
    ".so",
    ".vbe",
    ".vbs",
    ".winmd",
    ".wsf",
    ".wsh"
)
$unexpectedPayloads = @(
    $files |
        Where-Object {
            $relativePath = $_.FullName.Substring($packagePrefix.Length)
            $_.Extension -in $executableOrAssemblyExtensions -and
            $relativePath -notin $expectedPayloadPaths
        } |
        ForEach-Object { $_.FullName.Substring($packagePrefix.Length) } |
        Sort-Object -Unique)
if ($unexpectedPayloads.Count -ne 0) {
    throw "Unexpected executable or assembly payloads are not allowed: $($unexpectedPayloads -join ', ')"
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
    Commit = $ExpectedCommit
    SelfContained = $true
    DiagnosticsEnabled = $false
}

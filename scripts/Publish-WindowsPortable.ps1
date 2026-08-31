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

function Remove-TrailingDirectorySeparator([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    while ($fullPath.Length -gt $pathRoot.Length -and
        ($fullPath.EndsWith(
                [IO.Path]::DirectorySeparatorChar.ToString(),
                [StringComparison]::Ordinal) -or
            $fullPath.EndsWith(
                [IO.Path]::AltDirectorySeparatorChar.ToString(),
                [StringComparison]::Ordinal))) {
        $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
    }

    return $fullPath
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = Remove-TrailingDirectorySeparator(
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

$packageDirectory = Remove-TrailingDirectorySeparator($packageDirectory)
if ([string]::Equals(
        $packageDirectory,
        $artifactRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not $packageDirectory.StartsWith(
        $artifactPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The portable package output must remain beneath $artifactRoot."
}

$packageParent = [IO.Directory]::GetParent($packageDirectory)
if ($null -eq $packageParent -or
    -not [string]::Equals(
        $packageParent.FullName,
        $artifactRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The portable package output must be a direct child of $artifactRoot."
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
    throw "The package output must not already exist: $packageDirectory"
}

$stagingDirectory = Join-Path `
    $artifactRoot `
    (".FileOrganizer-staging-" + [Guid]::NewGuid().ToString("N"))

$headOutput = @(& git -C $repositoryRoot rev-parse --verify HEAD 2>&1)
$headExitCode = $LASTEXITCODE
if ($headExitCode -ne 0) {
    throw "Unable to resolve the release commit with Git (exit $headExitCode): $($headOutput -join [Environment]::NewLine)"
}

$headCommit = ($headOutput -join "").Trim()
if ($headCommit -notmatch '^[0-9A-Fa-f]{40}$') {
    throw "Git returned an invalid full commit SHA: $headCommit"
}
$headCommit = $headCommit.ToLowerInvariant()

$worktreeStatus = @(
    & git -C $repositoryRoot status --porcelain=v1 --untracked-files=all 2>&1)
$statusExitCode = $LASTEXITCODE
if ($statusExitCode -ne 0) {
    throw "Unable to inspect the release working tree with Git (exit $statusExitCode): $($worktreeStatus -join [Environment]::NewLine)"
}
if ($worktreeStatus.Count -ne 0) {
    throw "The release working tree must be clean, including non-ignored untracked files. Review and commit or remove every pending path before publishing."
}

$systemTemporaryRoot = Remove-TrailingDirectorySeparator([IO.Path]::GetTempPath())
$isolatedRoot = [IO.Path]::GetFullPath(
    (Join-Path $systemTemporaryRoot (
        "FileOrganizer-release-" + [Guid]::NewGuid().ToString("N"))))
$isolatedParent = [IO.Directory]::GetParent($isolatedRoot)
if ($null -eq $isolatedParent -or
    -not [string]::Equals(
        $isolatedParent.FullName,
        $systemTemporaryRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($isolatedRoot) -notmatch
        '^FileOrganizer-release-[0-9a-f]{32}$') {
    throw "The isolated release workspace could not be constrained beneath the system temporary directory."
}

$sourceArchive = Join-Path $isolatedRoot "source.zip"
$isolatedSource = Join-Path $isolatedRoot "source"
$isolatedPackages = Join-Path $isolatedRoot "packages"
$isolatedSolution = Join-Path $isolatedSource "FileOrganizer.slnx"
$isolatedProject = Join-Path `
    $isolatedSource `
    "src\FileOrganizer.App\FileOrganizer.App.csproj"
$isolatedBuildProps = Join-Path $isolatedSource "Directory.Build.props"
$isolatedPackagesProps = Join-Path $isolatedSource "Directory.Packages.props"
$isolatedGlobalJson = Join-Path $isolatedSource "global.json"
$isolatedNuGetConfig = Join-Path `
    $isolatedSource `
    "scripts\NuGet.Release.Config"
$isolatedVerifier = Join-Path `
    $isolatedSource `
    "scripts\Test-WindowsPortablePackage.ps1"
$locationPushed = $false
$cleanupProblems = [System.Collections.Generic.List[string]]::new()

try {
    $null = New-Item -ItemType Directory -Path $isolatedRoot
    $archiveOutput = @(
        & git -C $repositoryRoot archive `
            --format=zip `
            "--output=$sourceArchive" `
            $headCommit 2>&1)
    $archiveExitCode = $LASTEXITCODE
    if ($archiveExitCode -ne 0) {
        throw "The committed release source could not be archived (exit $archiveExitCode): $($archiveOutput -join [Environment]::NewLine)"
    }

    Expand-Archive -LiteralPath $sourceArchive -DestinationPath $isolatedSource
    foreach ($requiredInput in @(
            $isolatedSolution,
            $isolatedProject,
            $isolatedBuildProps,
            $isolatedPackagesProps,
            $isolatedGlobalJson,
            $isolatedNuGetConfig,
            $isolatedVerifier)) {
        if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf)) {
            throw "The committed release source is missing required input: $requiredInput"
        }
    }

    Push-Location -LiteralPath $isolatedSource
    $locationPushed = $true

    $solutionRestoreArguments = @(
        "restore",
        $isolatedSolution,
        "--packages", $isolatedPackages,
        "--configfile", $isolatedNuGetConfig,
        "--no-cache",
        "--force-evaluate",
        "--disable-build-servers",
        "-p:DirectoryBuildPropsPath=$isolatedBuildProps",
        "-p:DirectoryPackagesPropsPath=$isolatedPackagesProps",
        "-p:ImportDirectoryBuildTargets=false"
    )
    & dotnet @solutionRestoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated solution restore failed with exit code $LASTEXITCODE."
    }

    $buildArguments = @(
        "build",
        $isolatedSolution,
        "-c", "Release",
        "--no-restore",
        "--disable-build-servers",
        "-p:ContinuousIntegrationBuild=true",
        "-p:SourceRevisionId=$headCommit",
        "-p:DirectoryBuildPropsPath=$isolatedBuildProps",
        "-p:DirectoryPackagesPropsPath=$isolatedPackagesProps",
        "-p:ImportDirectoryBuildTargets=false"
    )
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated solution build failed with exit code $LASTEXITCODE."
    }

    $testArguments = @(
        "test",
        $isolatedSolution,
        "-c", "Release",
        "--no-build",
        "--disable-build-servers",
        "--logger", "console;verbosity=minimal",
        "-p:DirectoryBuildPropsPath=$isolatedBuildProps",
        "-p:DirectoryPackagesPropsPath=$isolatedPackagesProps",
        "-p:ImportDirectoryBuildTargets=false"
    )
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated solution test gate failed with exit code $LASTEXITCODE."
    }

    $restoreArguments = @(
        "restore",
        $isolatedProject,
        "-r", "win-x64",
        "--packages", $isolatedPackages,
        "--configfile", $isolatedNuGetConfig,
        "--no-cache",
        "--force-evaluate",
        "--disable-build-servers",
        "-p:DirectoryBuildPropsPath=$isolatedBuildProps",
        "-p:DirectoryPackagesPropsPath=$isolatedPackagesProps",
        "-p:ImportDirectoryBuildTargets=false"
    )
    & dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated win-x64 restore failed with exit code $LASTEXITCODE."
    }

    if (Test-Path -LiteralPath $packageDirectory) {
        throw "The package output appeared before publication; refusing replacement: $packageDirectory"
    }

    $null = [IO.Directory]::CreateDirectory($artifactRoot)
    $artifactEntry = Get-Item -LiteralPath $artifactRoot -Force
    if (($artifactEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The artifact root became a reparse point: $artifactRoot"
    }

    if (Test-Path -LiteralPath $stagingDirectory) {
        throw "The unique staging directory already exists: $stagingDirectory"
    }

    $null = [IO.Directory]::CreateDirectory($stagingDirectory)
    $stagingEntry = Get-Item -LiteralPath $stagingDirectory -Force
    if (($stagingEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The package staging directory is a reparse point: $stagingDirectory"
    }

    $publishArguments = @(
        "publish",
        $isolatedProject,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "--disable-build-servers",
        "-p:ContinuousIntegrationBuild=true",
        "-p:SourceRevisionId=$headCommit",
        "-p:DirectoryBuildPropsPath=$isolatedBuildProps",
        "-p:DirectoryPackagesPropsPath=$isolatedPackagesProps",
        "-p:ImportDirectoryBuildTargets=false",
        "-o", $stagingDirectory
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated win-x64 publish failed with exit code $LASTEXITCODE."
    }

    & $isolatedVerifier `
        -PackageDirectory $stagingDirectory `
        -ExpectedCommit $headCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Portable package verification failed with exit code $LASTEXITCODE."
    }

    if (Test-Path -LiteralPath $packageDirectory) {
        throw "The package output appeared before publication; refusing replacement: $packageDirectory"
    }

    try {
        [IO.Directory]::Move($stagingDirectory, $packageDirectory)
    }
    catch {
        throw "The verified package could not be published without replacing an existing destination: $($_.Exception.Message)"
    }

    & $isolatedVerifier `
        -PackageDirectory $packageDirectory `
        -ExpectedCommit $headCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Published portable package verification failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($locationPushed) {
        try {
            Pop-Location
            $locationPushed = $false
        }
        catch {
            $cleanupProblems.Add(
                "The isolated working directory could not be left: $($_.Exception.Message)")
            Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
        }
    }

    if (Test-Path -LiteralPath $stagingDirectory) {
        $stagingCleanupTarget = [IO.Path]::GetFullPath($stagingDirectory)
        $stagingCleanupParent = [IO.Directory]::GetParent($stagingCleanupTarget)
        $stagingCleanupName = [IO.Path]::GetFileName($stagingCleanupTarget)
        if ($null -eq $stagingCleanupParent -or
            -not [string]::Equals(
                $stagingCleanupParent.FullName,
                $artifactRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            $stagingCleanupName -notmatch
                '^\.FileOrganizer-staging-[0-9a-f]{32}$') {
            $cleanupProblems.Add(
                "Refusing to clean an unconstrained package staging path: $stagingCleanupTarget")
            Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
        }
        elseif (((Get-Item -LiteralPath $stagingCleanupTarget -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $cleanupProblems.Add(
                "Refusing to recursively clean a reparse package staging path: $stagingCleanupTarget")
            Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
        }
        else {
            try {
                Remove-Item -LiteralPath $stagingCleanupTarget -Recurse -Force
            }
            catch {
                $cleanupProblems.Add(
                    "The package staging directory could not be cleaned: $($_.Exception.Message)")
                Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
            }
        }
    }

    if (Test-Path -LiteralPath $isolatedRoot) {
        $cleanupTarget = [IO.Path]::GetFullPath($isolatedRoot)
        $cleanupParent = [IO.Directory]::GetParent($cleanupTarget)
        $cleanupName = [IO.Path]::GetFileName($cleanupTarget)
        if ($locationPushed -or
            $null -eq $cleanupParent -or
            -not [string]::Equals(
                $cleanupParent.FullName,
                $systemTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            $cleanupName -notmatch '^FileOrganizer-release-[0-9a-f]{32}$') {
            $cleanupProblems.Add(
                "Refusing to clean an unconstrained or active release workspace: $cleanupTarget")
            Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
        }
        elseif (((Get-Item -LiteralPath $cleanupTarget -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $cleanupProblems.Add(
                "Refusing to recursively clean a reparse release workspace: $cleanupTarget")
            Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
        }
        else {
            try {
                Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
            }
            catch {
                $cleanupProblems.Add(
                    "The isolated release workspace could not be cleaned: $($_.Exception.Message)")
                Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
            }
        }
    }
}

$postPublishHeadOutput = @(& git -C $repositoryRoot rev-parse --verify HEAD 2>&1)
$postPublishHeadExitCode = $LASTEXITCODE
if ($postPublishHeadExitCode -ne 0) {
    throw "Unable to recheck the release commit with Git (exit $postPublishHeadExitCode): $($postPublishHeadOutput -join [Environment]::NewLine)"
}

$postPublishHead = ($postPublishHeadOutput -join "").Trim()
if (-not [string]::Equals(
        $postPublishHead,
        $headCommit,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The release commit changed during publish. The candidate is not accepted: expected $headCommit, observed $postPublishHead."
}

$postPublishStatus = @(
    & git -C $repositoryRoot status --porcelain=v1 --untracked-files=all 2>&1)
$postPublishStatusExitCode = $LASTEXITCODE
if ($postPublishStatusExitCode -ne 0) {
    throw "Unable to recheck the release working tree with Git (exit $postPublishStatusExitCode): $($postPublishStatus -join [Environment]::NewLine)"
}
if ($postPublishStatus.Count -ne 0) {
    throw "The release working tree changed during publish. The candidate is not accepted."
}

if ($cleanupProblems.Count -ne 0) {
    throw "Release cleanup did not complete, so the candidate is not accepted. Review the preceding warning(s)."
}

Write-Host "Portable package ready for commit $headCommit`: $packageDirectory"

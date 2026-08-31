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

function Test-RetryableDirectoryCleanupError(
    [Management.Automation.ErrorRecord] $ErrorRecord) {
    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        if ($exception -is [IO.IOException]) {
            $win32Error = $exception.HResult -band 0xffff
            if ($win32Error -eq 32 -or $win32Error -eq 33) {
                return $true
            }
        }

        $exception = $exception.InnerException
    }

    return $false
}

function Remove-ValidatedDirectoryTreeWithRetry(
    [string] $Path,
    [string] $ExpectedParent,
    [string] $TrustedBoundary,
    [string] $ExpectedNamePattern,
    [string] $TargetDescription,
    [int] $MaximumAttempts = 6,
    [int] $RetryDelayMilliseconds = 200,
    [scriptblock] $DelayAction = {
        param([int] $Milliseconds)
        [Threading.Thread]::Sleep($Milliseconds)
    }) {
    if ($MaximumAttempts -lt 1 -or $RetryDelayMilliseconds -lt 0) {
        throw "Directory cleanup retry settings are invalid."
    }

    $cleanupTarget = [IO.Path]::GetFullPath($Path)
    $expectedParentPath = Remove-TrailingDirectorySeparator($ExpectedParent)
    $trustedBoundaryPath = Remove-TrailingDirectorySeparator($TrustedBoundary)
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $cleanupTarget)) {
            return
        }

        $cleanupParent = [IO.Directory]::GetParent($cleanupTarget)
        $cleanupName = [IO.Path]::GetFileName($cleanupTarget)
        if ($null -eq $cleanupParent -or
            -not [string]::Equals(
                $cleanupParent.FullName,
                $expectedParentPath,
                [StringComparison]::OrdinalIgnoreCase) -or
            $cleanupName -notmatch $ExpectedNamePattern) {
            throw "Refusing to clean an unconstrained $TargetDescription`: $cleanupTarget"
        }

        $currentBoundaryPath = $expectedParentPath
        $reachedTrustedBoundary = $false
        while ($null -ne $currentBoundaryPath) {
            $boundaryEntry = Get-Item -LiteralPath $currentBoundaryPath -Force
            if (-not $boundaryEntry.PSIsContainer -or
                ($boundaryEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to clean through an invalid or reparse boundary: $currentBoundaryPath"
            }
            if ([string]::Equals(
                    $currentBoundaryPath,
                    $trustedBoundaryPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                $reachedTrustedBoundary = $true
                break
            }

            $boundaryParent = [IO.Directory]::GetParent($currentBoundaryPath)
            $currentBoundaryPath = if ($null -eq $boundaryParent) {
                $null
            }
            else {
                $boundaryParent.FullName
            }
        }
        if (-not $reachedTrustedBoundary) {
            throw "Refusing cleanup outside the trusted boundary: $trustedBoundaryPath"
        }

        $cleanupEntry = Get-Item -LiteralPath $cleanupTarget -Force
        if (-not $cleanupEntry.PSIsContainer -or
            ($cleanupEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively clean a reparse $TargetDescription`: $cleanupTarget"
        }

        try {
            Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
            return
        }
        catch {
            $removeError = $_
            if (-not (Test-Path -LiteralPath $cleanupTarget)) {
                return
            }
            if ($attempt -eq $MaximumAttempts -or
                -not (Test-RetryableDirectoryCleanupError $removeError)) {
                throw
            }

            & $DelayAction $RetryDelayMilliseconds
        }
    }
}

function Set-ProcessEnvironmentVariable(
    [string] $Name,
    [AllowNull()]
    [object] $Value) {
    $environmentValue = if ($null -eq $Value) {
        [Management.Automation.Language.NullString]::Value
    }
    else {
        [string] $Value
    }

    [Environment]::SetEnvironmentVariable(
        $Name,
        $environmentValue,
        [EnvironmentVariableTarget]::Process)
}

function Write-ExactGitBlob(
    [string] $GitDirectory,
    [string] $ObjectId,
    [string] $DestinationPath) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "git"
    $startInfo.Arguments = "cat-file blob $ObjectId"
    $startInfo.WorkingDirectory = $GitDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Git could not be started to read committed blob $ObjectId."
        }

        $errorTask = $process.StandardError.ReadToEndAsync()
        try {
            $destinationStream = [IO.File]::Open(
                $DestinationPath,
                [IO.FileMode]::Create,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $process.StandardOutput.BaseStream.CopyTo($destinationStream)
                $destinationStream.Flush($true)
            }
            finally {
                $destinationStream.Dispose()
            }

            $process.WaitForExit()
            $errorText = $errorTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) {
                throw "Git could not read committed blob $ObjectId (exit $($process.ExitCode)): $errorText"
            }
        }
        catch {
            if (Test-Path -LiteralPath $DestinationPath -PathType Leaf) {
                Remove-Item -LiteralPath $DestinationPath -Force
            }
            throw
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-ExactCommittedSourceTree(
    [string] $GitDirectory,
    [string] $Commit,
    [string] $ExtractedSourceRoot,
    [string] $EmptyAttributesFile) {
    $treeFormat = "%(objectmode)%x09%(objecttype)%x09%(objectname)%x09%(path)"
    $treeOutput = & git `
        --no-replace-objects `
        -c core.quotePath=false `
        -c "core.attributesFile=$EmptyAttributesFile" `
        "--git-dir=$GitDirectory" `
        ls-tree `
        -r `
        -z `
        --full-tree `
        "--format=$treeFormat" `
        $Commit 2>&1
    $treeExitCode = $LASTEXITCODE
    if ($treeExitCode -ne 0) {
        throw "Unable to enumerate the exact committed tree (exit $treeExitCode): $($treeOutput -join [Environment]::NewLine)"
    }
    if ($treeOutput -is [array]) {
        throw "The committed tree contains a line-break path that this Windows release workflow does not support."
    }

    $treeText = [string] $treeOutput
    if ([string]::IsNullOrEmpty($treeText) -or
        -not $treeText.EndsWith([char] 0)) {
        throw "Git returned an invalid NUL-delimited committed tree."
    }

    $expectedFiles = [System.Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    foreach ($entry in $treeText.Split(
            [char[]] @([char] 0),
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $parts = $entry -split "`t", 4
        if ($parts.Count -ne 4) {
            throw "Git returned an invalid committed-tree entry."
        }

        $objectMode = $parts[0]
        $objectType = $parts[1]
        $objectId = $parts[2]
        $gitPath = $parts[3]
        if (($objectMode -ne "100644" -and $objectMode -ne "100755") -or
            $objectType -ne "blob") {
            throw "The committed release tree may contain only regular files: $gitPath ($objectMode $objectType)"
        }
        if ($objectId -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
            throw "Git returned an invalid object ID for committed path: $gitPath"
        }
        if ([string]::IsNullOrWhiteSpace($gitPath) -or
            [IO.Path]::IsPathRooted($gitPath) -or
            $gitPath.Contains("\")) {
            throw "Git returned an unsafe committed path: $gitPath"
        }

        $segments = $gitPath.Split([char] '/')
        $invalidSegments = @(
            $segments | Where-Object {
                [string]::IsNullOrEmpty($_) -or
                $_ -eq "." -or
                $_ -eq ".." -or
                $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
            })
        if ($segments.Count -eq 0 -or $invalidSegments.Count -ne 0) {
            throw "Git returned an unsupported committed path: $gitPath"
        }
        if ($expectedFiles.ContainsKey($gitPath)) {
            throw "Git returned a duplicate committed path: $gitPath"
        }

        $attributeOutput = & git `
            --no-replace-objects `
            -c core.quotePath=false `
            -c "core.attributesFile=$EmptyAttributesFile" `
            "--git-dir=$GitDirectory" `
            check-attr `
            -z `
            "--source=$Commit" `
            export-ignore `
            export-subst `
            -- `
            $gitPath 2>&1
        $attributeExitCode = $LASTEXITCODE
        if ($attributeExitCode -ne 0) {
            throw "Unable to inspect archive attributes for committed path $gitPath (exit $attributeExitCode): $($attributeOutput -join [Environment]::NewLine)"
        }
        if ($attributeOutput -is [array]) {
            throw "Git returned invalid archive attributes for committed path: $gitPath"
        }

        $attributeParts = ([string] $attributeOutput).Split(
            [char[]] @([char] 0),
            [StringSplitOptions]::RemoveEmptyEntries)
        if ($attributeParts.Count -ne 6 -or
            $attributeParts[0] -ne $gitPath -or
            $attributeParts[1] -ne "export-ignore" -or
            $attributeParts[3] -ne $gitPath -or
            $attributeParts[4] -ne "export-subst") {
            throw "Git returned malformed archive attributes for committed path: $gitPath"
        }
        foreach ($attributeValue in @($attributeParts[2], $attributeParts[5])) {
            if ($attributeValue -ne "unspecified" -and
                $attributeValue -ne "unset") {
                throw "The committed release tree cannot use export-ignore or export-subst: $gitPath"
            }
        }

        $expectedFiles.Add($gitPath, $objectId)
    }

    $sourceRoot = Remove-TrailingDirectorySeparator($ExtractedSourceRoot)
    $sourcePrefix = $sourceRoot + [IO.Path]::DirectorySeparatorChar
    $actualFiles = [System.Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The extracted committed source contains a reparse entry: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            continue
        }
        if (-not $item.FullName.StartsWith(
                $sourcePrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The extracted committed source escaped its root: $($item.FullName)"
        }

        $relativePath = $item.FullName.Substring($sourcePrefix.Length).Replace(
            [IO.Path]::DirectorySeparatorChar,
            [char] '/')
        if ($actualFiles.ContainsKey($relativePath)) {
            throw "The extracted committed source contains a duplicate logical path: $relativePath"
        }
        $actualFiles.Add($relativePath, $item.FullName)
    }

    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw "The extracted source file count does not match the exact committed tree (expected=$($expectedFiles.Count), actual=$($actualFiles.Count))."
    }

    foreach ($expectedEntry in $expectedFiles.GetEnumerator()) {
        if (-not $actualFiles.ContainsKey($expectedEntry.Key)) {
            throw "The extracted source is missing a committed file: $($expectedEntry.Key)"
        }

        $actualPath = $actualFiles[$expectedEntry.Key]
        Write-ExactGitBlob `
            -GitDirectory $GitDirectory `
            -ObjectId $expectedEntry.Value `
            -DestinationPath $actualPath

        $actualObjectOutput = @(
            & git `
                --no-replace-objects `
                "--git-dir=$GitDirectory" `
                hash-object `
                --no-filters `
                -- `
                $actualPath 2>&1)
        $actualObjectExitCode = $LASTEXITCODE
        $actualObjectId = ($actualObjectOutput -join "").Trim()
        if ($actualObjectExitCode -ne 0 -or
            -not [string]::Equals(
                $actualObjectId,
                $expectedEntry.Value,
                [StringComparison]::Ordinal)) {
            throw "The extracted source bytes do not match committed blob $($expectedEntry.Value): $($expectedEntry.Key)"
        }
    }
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
$isolatedGitRepository = Join-Path $isolatedRoot "repository.git"
$isolatedGitTemplate = Join-Path $isolatedRoot "git-template"
$isolatedGitConfig = Join-Path $isolatedRoot "git-global.config"
$isolatedGitAttributes = Join-Path $isolatedRoot "git-global.attributes"
$isolatedSolution = Join-Path $isolatedSource "FileOrganizer.slnx"
$isolatedProject = Join-Path `
    $isolatedSource `
    "src\FileOrganizer.App\FileOrganizer.App.csproj"
$isolatedAppTests = Join-Path `
    $isolatedSource `
    "tests\FileOrganizer.App.Tests\FileOrganizer.App.Tests.csproj"
$isolatedBuildProps = Join-Path $isolatedSource "Directory.Build.props"
$isolatedPackagesProps = Join-Path $isolatedSource "Directory.Packages.props"
$isolatedGlobalJson = Join-Path $isolatedSource "global.json"
$isolatedNuGetConfig = Join-Path `
    $isolatedSource `
    "scripts\NuGet.Release.Config"
$isolatedVerifier = Join-Path `
    $isolatedSource `
    "scripts\Test-WindowsPortablePackage.ps1"
$isolatedPublisher = Join-Path `
    $isolatedSource `
    "scripts\Publish-WindowsPortable.ps1"
$locationPushed = $false
$cleanupProblems = [System.Collections.Generic.List[string]]::new()

$gitEnvironmentOverrides = [ordered] @{
    GIT_CONFIG_NOSYSTEM = "1"
    GIT_CONFIG_GLOBAL = $isolatedGitConfig
    GIT_CONFIG_COUNT = "0"
    GIT_CONFIG_PARAMETERS = $null
    GIT_ATTR_NOSYSTEM = "1"
    GIT_ATTR_SOURCE = $null
    GIT_NO_REPLACE_OBJECTS = "1"
    GIT_DIR = $null
    GIT_WORK_TREE = $null
    GIT_INDEX_FILE = $null
    GIT_OBJECT_DIRECTORY = $null
    GIT_ALTERNATE_OBJECT_DIRECTORIES = $null
    GIT_COMMON_DIR = $null
    GIT_NAMESPACE = $null
    GIT_REPLACE_REF_BASE = $null
    GIT_SHALLOW_FILE = $null
    GIT_EXEC_PATH = $null
}
$previousGitEnvironment = [ordered] @{}
$appliedGitEnvironmentNames = [System.Collections.Generic.List[string]]::new()

try {
    foreach ($environmentName in $gitEnvironmentOverrides.Keys) {
        $previousGitEnvironment[$environmentName] =
            [Environment]::GetEnvironmentVariable(
                $environmentName,
                [EnvironmentVariableTarget]::Process)
        $appliedGitEnvironmentNames.Add($environmentName)
        Set-ProcessEnvironmentVariable `
            -Name $environmentName `
            -Value $gitEnvironmentOverrides[$environmentName]
    }

    $headOutput = @(
        & git --no-replace-objects -C $repositoryRoot rev-parse --verify HEAD 2>&1)
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
        & git `
            --no-replace-objects `
            -c "core.attributesFile=$isolatedGitAttributes" `
            -C $repositoryRoot `
            status `
            --porcelain=v1 `
            --untracked-files=all 2>&1)
    $statusExitCode = $LASTEXITCODE
    if ($statusExitCode -ne 0) {
        throw "Unable to inspect the release working tree with Git (exit $statusExitCode): $($worktreeStatus -join [Environment]::NewLine)"
    }
    if ($worktreeStatus.Count -ne 0) {
        throw "The release working tree must be clean, including non-ignored untracked files. Review and commit or remove every pending path before publishing."
    }

    try {
    $null = New-Item -ItemType Directory -Path $isolatedRoot
    $null = [IO.Directory]::CreateDirectory($isolatedGitTemplate)
    [IO.File]::WriteAllText($isolatedGitConfig, "")
    [IO.File]::WriteAllText($isolatedGitAttributes, "")

    $cloneOutput = @(
            & git `
                --no-replace-objects `
                -c "core.attributesFile=$isolatedGitAttributes" `
                clone `
                --bare `
                --no-local `
                "--template=$isolatedGitTemplate" `
                $repositoryRoot `
                $isolatedGitRepository 2>&1)
        $cloneExitCode = $LASTEXITCODE
        if ($cloneExitCode -ne 0) {
            throw "The exact committed repository could not be isolated (exit $cloneExitCode): $($cloneOutput -join [Environment]::NewLine)"
        }

        $isolatedCommitOutput = @(
            & git `
                --no-replace-objects `
                "--git-dir=$isolatedGitRepository" `
                rev-parse `
                --verify `
                "$headCommit^{commit}" 2>&1)
        $isolatedCommitExitCode = $LASTEXITCODE
        $isolatedCommit = ($isolatedCommitOutput -join "").Trim()
        if ($isolatedCommitExitCode -ne 0 -or
            -not [string]::Equals(
                $isolatedCommit,
                $headCommit,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The isolated repository does not contain the exact release commit $headCommit."
        }

        $archiveOutput = @(
            & git `
                --no-replace-objects `
                -c "core.attributesFile=$isolatedGitAttributes" `
                "--git-dir=$isolatedGitRepository" `
                archive `
                --format=zip `
                "--output=$sourceArchive" `
                $headCommit 2>&1)
        $archiveExitCode = $LASTEXITCODE
        if ($archiveExitCode -ne 0) {
            throw "The committed release source could not be archived (exit $archiveExitCode): $($archiveOutput -join [Environment]::NewLine)"
        }

        Expand-Archive -LiteralPath $sourceArchive -DestinationPath $isolatedSource
    Assert-ExactCommittedSourceTree `
        -GitDirectory $isolatedGitRepository `
        -Commit $headCommit `
        -ExtractedSourceRoot $isolatedSource `
        -EmptyAttributesFile $isolatedGitAttributes

    foreach ($requiredInput in @(
            $isolatedSolution,
            $isolatedProject,
            $isolatedAppTests,
            $isolatedBuildProps,
            $isolatedPackagesProps,
            $isolatedGlobalJson,
            $isolatedNuGetConfig,
            $isolatedVerifier,
            $isolatedPublisher)) {
        if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf)) {
            throw "The committed release source is missing required input: $requiredInput"
        }
    }

    $livePublisherText = [IO.File]::ReadAllText($PSCommandPath).Replace(
        "`r`n",
        "`n").Replace("`r", "`n")
    $committedPublisherText = [IO.File]::ReadAllText($isolatedPublisher).Replace(
        "`r`n",
        "`n").Replace("`r", "`n")
    if (-not [string]::Equals(
            $livePublisherText,
            $committedPublisherText,
            [StringComparison]::Ordinal)) {
        throw "The running publish orchestrator differs from the exact committed script."
    }

    Push-Location -LiteralPath $isolatedSource
    $locationPushed = $true

    $solutionRestoreArguments = @(
        "restore",
        $isolatedSolution,
        "--packages", $isolatedPackages,
        "--configfile", $isolatedNuGetConfig,
        "--no-cache",
        "--locked-mode",
        "--disable-build-servers",
        "-p:DirectoryBuildPropsPath=$isolatedBuildProps",
        "-p:DirectoryPackagesPropsPath=$isolatedPackagesProps",
        "-p:ImportDirectoryBuildTargets=false"
    )
    & dotnet @solutionRestoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated solution restore failed with exit code $LASTEXITCODE."
    }

    $auditArguments = @(
        "package",
        "list",
        "--project", $isolatedSolution,
        "--vulnerable",
        "--include-transitive",
        "--no-restore",
        "--config", $isolatedNuGetConfig,
        "--format", "json",
        "--output-version", "1"
    )
    $auditOutput = @(& dotnet @auditArguments)
    if ($LASTEXITCODE -ne 0) {
        throw "The isolated NuGet vulnerability audit failed with exit code $LASTEXITCODE."
    }
    $auditText = $auditOutput -join [Environment]::NewLine
    try {
        $auditDocument = ConvertFrom-Json -InputObject $auditText
    }
    catch {
        throw "The isolated NuGet vulnerability audit returned malformed JSON: $($_.Exception.Message)"
    }
    if ($null -eq $auditDocument -or
        $null -eq $auditDocument.PSObject.Properties["version"] -or
        [int] $auditDocument.version -ne 1 -or
        $null -eq $auditDocument.PSObject.Properties["projects"] -or
        $null -eq $auditDocument.PSObject.Properties["sources"]) {
        throw "The isolated NuGet vulnerability audit returned an unsupported JSON schema."
    }

    $auditSources = @($auditDocument.sources)
    if ($auditSources.Count -ne 1 -or
        -not [string]::Equals(
            [string] $auditSources[0],
            "https://api.nuget.org/v3/index.json",
            [StringComparison]::Ordinal)) {
        throw "The isolated NuGet vulnerability audit used an unexpected package source."
    }

    $expectedAuditProjectPaths = @(
        "src\FileOrganizer.App\FileOrganizer.App.csproj",
        "src\FileOrganizer.Application\FileOrganizer.Application.csproj",
        "src\FileOrganizer.Core\FileOrganizer.Core.csproj",
        "src\FileOrganizer.Infrastructure\FileOrganizer.Infrastructure.csproj",
        "tests\FileOrganizer.App.Tests\FileOrganizer.App.Tests.csproj",
        "tests\FileOrganizer.Application.Tests\FileOrganizer.Application.Tests.csproj",
        "tests\FileOrganizer.Core.Tests\FileOrganizer.Core.Tests.csproj",
        "tests\FileOrganizer.Infrastructure.LeaseProbe\FileOrganizer.Infrastructure.LeaseProbe.csproj",
        "tests\FileOrganizer.Infrastructure.Tests\FileOrganizer.Infrastructure.Tests.csproj"
    )
    $expectedAuditProjects = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeProjectPath in $expectedAuditProjectPaths) {
        $null = $expectedAuditProjects.Add(
            [IO.Path]::GetFullPath((Join-Path $isolatedSource $relativeProjectPath)))
    }
    $actualAuditProjects = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($auditProject in @($auditDocument.projects)) {
        if ($null -eq $auditProject -or
            $null -eq $auditProject.PSObject.Properties["path"] -or
            [string]::IsNullOrWhiteSpace([string] $auditProject.path)) {
            throw "The isolated NuGet vulnerability audit returned a project without a path."
        }
        $actualAuditProject = [IO.Path]::GetFullPath([string] $auditProject.path)
        if (-not $actualAuditProjects.Add($actualAuditProject)) {
            throw "The isolated NuGet vulnerability audit returned a duplicate project: $actualAuditProject"
        }
    }
    if ($actualAuditProjects.Count -ne $expectedAuditProjects.Count) {
        throw "The isolated NuGet vulnerability audit returned an incomplete project set."
    }
    foreach ($expectedAuditProject in $expectedAuditProjects) {
        if (-not $actualAuditProjects.Contains($expectedAuditProject)) {
            throw "The isolated NuGet vulnerability audit omitted project: $expectedAuditProject"
        }
    }
    if ($auditText -match '"vulnerabilities"\s*:') {
        throw "The isolated dependency graph contains a known vulnerability."
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
        "--locked-mode",
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

    $publishedAppAssembly = Join-Path $stagingDirectory "FileOrganizer.App.dll"
    if (-not (Test-Path -LiteralPath $publishedAppAssembly -PathType Leaf)) {
        throw "The published desktop assembly is missing before boundary verification: $publishedAppAssembly"
    }

    $artifactBoundaryResultsDirectory = Join-Path `
        $isolatedRoot `
        "published-app-boundary-results"
    if (Test-Path -LiteralPath $artifactBoundaryResultsDirectory) {
        throw "The published desktop boundary result directory already exists: $artifactBoundaryResultsDirectory"
    }
    $null = [IO.Directory]::CreateDirectory($artifactBoundaryResultsDirectory)
    $artifactBoundaryResultName = "PublishedDesktopMutationBoundary.trx"
    $artifactBoundaryResultPath = Join-Path `
        $artifactBoundaryResultsDirectory `
        $artifactBoundaryResultName
    $artifactBoundaryTestFilter =
        "FullyQualifiedName=FileOrganizer.App.Tests.Architecture.DesktopMutationBoundaryTests.ProductionAssemblyDoesNotReferenceExecutionSurfaces" +
        "|FullyQualifiedName=FileOrganizer.App.Tests.Architecture.DesktopMutationBoundaryTests.ProductionAssemblyDoesNotBindDirectFilesystemMutationOrEscapeApis"

    $assemblyUnderTestVariable = "FILEORGANIZER_DESKTOP_ASSEMBLY_UNDER_TEST"
    $previousAssemblyUnderTest = [Environment]::GetEnvironmentVariable(
        $assemblyUnderTestVariable,
        [EnvironmentVariableTarget]::Process)
    try {
        Set-ProcessEnvironmentVariable `
            -Name $assemblyUnderTestVariable `
            -Value $publishedAppAssembly
        $artifactBoundaryTestArguments = @(
            "test",
            $isolatedAppTests,
            "-c", "Release",
            "--no-build",
            "--no-restore",
            "--disable-build-servers",
            "--logger", "trx;LogFileName=$artifactBoundaryResultName",
            "--results-directory", $artifactBoundaryResultsDirectory,
            "--filter", $artifactBoundaryTestFilter,
            "-p:DirectoryBuildPropsPath=$isolatedBuildProps",
            "-p:DirectoryPackagesPropsPath=$isolatedPackagesProps",
            "-p:ImportDirectoryBuildTargets=false"
        )
        & dotnet @artifactBoundaryTestArguments
        if ($LASTEXITCODE -ne 0) {
            throw "The published desktop mutation-boundary gate failed with exit code $LASTEXITCODE."
        }

        if (-not (Test-Path -LiteralPath $artifactBoundaryResultPath -PathType Leaf)) {
            throw "The published desktop mutation-boundary gate produced no deterministic TRX result."
        }

        try {
            [xml] $artifactBoundaryResult = [IO.File]::ReadAllText(
                $artifactBoundaryResultPath)
            $artifactBoundaryCounters = $artifactBoundaryResult.SelectSingleNode(
                "/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
            if ($null -eq $artifactBoundaryCounters) {
                throw "The TRX result has no Counters element."
            }

            $artifactBoundaryTotal = [int] $artifactBoundaryCounters.GetAttribute("total")
            $artifactBoundaryExecuted = [int] $artifactBoundaryCounters.GetAttribute("executed")
            $artifactBoundaryPassed = [int] $artifactBoundaryCounters.GetAttribute("passed")
            $artifactBoundaryFailed = [int] $artifactBoundaryCounters.GetAttribute("failed")
        }
        catch {
            throw "The published desktop mutation-boundary TRX result is invalid: $($_.Exception.Message)"
        }

        if ($artifactBoundaryTotal -ne 2 -or
            $artifactBoundaryExecuted -ne 2 -or
            $artifactBoundaryPassed -ne 2 -or
            $artifactBoundaryFailed -ne 0) {
            throw "The published desktop mutation-boundary gate did not execute and pass its two required artifact scans (total=$artifactBoundaryTotal, executed=$artifactBoundaryExecuted, passed=$artifactBoundaryPassed, failed=$artifactBoundaryFailed)."
        }
    }
    finally {
        Set-ProcessEnvironmentVariable `
            -Name $assemblyUnderTestVariable `
            -Value $previousAssemblyUnderTest
    }

    & $isolatedVerifier `
        -PackageDirectory $stagingDirectory `
        -ExpectedCommit $headCommit

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
        try {
            Remove-ValidatedDirectoryTreeWithRetry `
                -Path $stagingDirectory `
                -ExpectedParent $artifactRoot `
                -TrustedBoundary $repositoryRoot `
                -ExpectedNamePattern '^\.FileOrganizer-staging-[0-9a-f]{32}$' `
                -TargetDescription "package staging path"
        }
        catch {
            $cleanupProblems.Add(
                "The package staging directory could not be cleaned: $($_.Exception.Message)")
            Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
        }
    }

    if (Test-Path -LiteralPath $isolatedRoot) {
        if ($locationPushed) {
            $cleanupProblems.Add(
                "Refusing to clean an active release workspace: $isolatedRoot")
            Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
        }
        else {
            try {
                Remove-ValidatedDirectoryTreeWithRetry `
                    -Path $isolatedRoot `
                    -ExpectedParent $systemTemporaryRoot `
                    -TrustedBoundary $systemTemporaryRoot `
                    -ExpectedNamePattern '^FileOrganizer-release-[0-9a-f]{32}$' `
                    -TargetDescription "release workspace"
            }
            catch {
                $cleanupProblems.Add(
                    "The isolated release workspace could not be cleaned: $($_.Exception.Message)")
                Write-Warning $cleanupProblems[$cleanupProblems.Count - 1]
            }
        }
    }
}

$postPublishHeadOutput = @(& git --no-replace-objects -C $repositoryRoot rev-parse --verify HEAD 2>&1)
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
    & git `
        --no-replace-objects `
        -c "core.attributesFile=$isolatedGitAttributes" `
        -C $repositoryRoot `
        status `
        --porcelain=v1 `
        --untracked-files=all 2>&1)
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
}
finally {
    foreach ($environmentName in $appliedGitEnvironmentNames) {
        Set-ProcessEnvironmentVariable `
            -Name $environmentName `
            -Value $previousGitEnvironment[$environmentName]
    }
}

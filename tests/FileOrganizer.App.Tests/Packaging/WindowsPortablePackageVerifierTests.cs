using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FileOrganizer.App.Tests.Packaging;

public sealed class WindowsPortablePackageVerifierTests
{
    private static readonly string[] FirstPartyBinaryFiles =
    [
        "FileOrganizer.App.exe",
        "FileOrganizer.App.dll",
        "FileOrganizer.Application.dll",
        "FileOrganizer.Core.dll",
        "FileOrganizer.Infrastructure.dll",
    ];

    private static readonly string[] RequiredBinaryFiles =
    [
        "FileOrganizer.App.exe",
        "FileOrganizer.App.dll",
        "FileOrganizer.Application.dll",
        "FileOrganizer.Core.dll",
        "FileOrganizer.Infrastructure.dll",
        "coreclr.dll",
        "clrjit.dll",
        "hostfxr.dll",
        "hostpolicy.dll",
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "Fixture.Runtime.dll",
        "Fixture.Native.dll",
        "Fixture.RuntimeTarget.dll",
        "Fixture.Data.dat",
    ];

    [Fact]
    public async Task CompleteSelfContainedDependencyFixturePassesVerification()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SelfContained", result.Output, StringComparison.Ordinal);
        Assert.Contains("DiagnosticsEnabled", result.Output, StringComparison.Ordinal);
        Assert.Contains(package.ExpectedCommit, result.Output, StringComparison.Ordinal);
        Assert.Contains("False", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageSymbolIsRejected()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        File.WriteAllBytes(Path.Combine(package.FullPath, "native-symbols.pdb"), [1]);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Release symbols leaked",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnabledReleaseDiagnosticsAreRejected()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: true);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "diagnostics are not explicitly disabled",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"false\"")]
    [InlineData("0")]
    public async Task NonBooleanDisabledDiagnosticsValueIsRejected(string jsonLiteral)
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        package.SetDiagnosticsJsonLiteral(jsonLiteral);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "diagnostics are not explicitly disabled",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Fixture.Runtime.dll")]
    [InlineData("Fixture.Native.dll")]
    [InlineData("Fixture.RuntimeTarget.dll")]
    [InlineData("fr\\Fixture.Resources.dll")]
    public async Task MissingDependencyAssetDeclaredBySelectedTargetIsRejected(
        string missingAsset)
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        File.Delete(Path.Combine(package.FullPath, missingAsset));

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "missing a dependency asset",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            missingAsset,
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscapingDependencyAssetPathIsRejected()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        package.ReplaceDependencyAssetPath(
            "lib/net10.0/Fixture.Runtime.dll",
            "../Escaped.dll");

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "invalid runtime asset path",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductVersionFromAnotherCommitIsRejected()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        string differentCommit = new('0', 40);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            differentCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "does not identify exact expected SourceRevisionId",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("FileOrganizer.Application.dll")]
    [InlineData("FileOrganizer.Core.dll")]
    [InlineData("FileOrganizer.Infrastructure.dll")]
    public async Task EveryFirstPartyAssemblyMustIdentifyTheExactExpectedRevision(
        string firstPartyAssembly)
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        File.WriteAllBytes(Path.Combine(package.FullPath, firstPartyAssembly), [1]);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(firstPartyAssembly, result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "exact expected SourceRevisionId",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonX64EntryExecutableIsRejected()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        package.SetEntryExecutableMachine(0xaa64);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "does not target x64",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.production")]
    [InlineData("signing.pem")]
    [InlineData("private.key")]
    [InlineData("certificate.pfx")]
    [InlineData("certificate.p12")]
    [InlineData("release.snk")]
    [InlineData("FolderProfile.pubxml")]
    [InlineData("service-credentials.json")]
    [InlineData("clientSecrets.json")]
    public async Task SecretProneExtraFileIsRejected(string fileName)
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        File.WriteAllText(Path.Combine(package.FullPath, fileName), "secret fixture");

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "secret-bearing files leaked",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fileName, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Injected.dll")]
    [InlineData("tools\\Injected.exe")]
    [InlineData("launch.cmd")]
    [InlineData("payload.ps1")]
    [InlineData("support.lnk")]
    [InlineData("support.url")]
    [InlineData("help.chm")]
    [InlineData("bootstrap.application")]
    [InlineData("settings.reg")]
    [InlineData("driver.inf")]
    [InlineData("README.txt")]
    [InlineData("FileOrganizer.App.runtimeconfig.dev.json")]
    public async Task UnexpectedPackagePayloadIsRejected(
        string relativePath)
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        string fullPath = Path.Combine(package.FullPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, [1]);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Unexpected package payloads",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(relativePath, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NamedAlternateDataStreamOnExpectedPayloadIsRejected()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        string volumeRoot = Path.GetPathRoot(package.FullPath)
            ?? throw new InvalidOperationException("Temporary package has no volume root.");
        string driveFormat;
        try
        {
            driveFormat = new DriveInfo(volumeRoot).DriveFormat;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip(
                $"The temporary package volume format is unavailable: {exception.GetType().Name}");
            return;
        }
        Assert.SkipUnless(
            string.Equals(driveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
            "NTFS alternate-data-stream test.");

        string payloadPath = Path.Combine(package.FullPath, "Fixture.Data.dat");
        string injectedStreamPath = $"{payloadPath}:Injected";
        try
        {
            File.WriteAllText(injectedStreamPath, "alternate stream fixture");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Assert.Skip(
                $"Alternate data streams are unavailable: {exception.GetType().Name}");
        }

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "alternate data streams",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Fixture.Data.dat:Injected",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\fileorganizer-rooted-relative")]
    [InlineData(@"C:relative")]
    public async Task RootedButNotFullyQualifiedPackagePathIsRejected(string packagePath)
    {
        RequireWindowsPortableVerifier();

        VerificationProcessResult result = await RunVerifierAsync(
            packagePath,
            new string('0', 40));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "must be an absolute path",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishScriptRejectsArtifactRootWithATrailingSeparator()
    {
        RequireWindowsPowerShell();
        string script = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Publish-WindowsPortable.ps1");
        Assert.True(File.Exists(script), $"Publish script was not copied to {script}.");
        string artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "artifacts",
            "portable") + Path.DirectorySeparatorChar;

        VerificationProcessResult result = await RunPowerShellAsync(
            script,
            "-OutputDirectory",
            artifactRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "must remain beneath",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishScriptAcceptsEquivalentWindowsPathCasing()
    {
        RequireWindowsPowerShell();
        string scratchRoot = Path.Combine(
            Path.GetTempPath(),
            "fileorganizer-publish-tests",
            Guid.NewGuid().ToString("N"));
        string releaseDirectory = Path.Combine(scratchRoot, "Release");
        Directory.CreateDirectory(releaseDirectory);
        try
        {
            string sourceScript = Path.Combine(
                AppContext.BaseDirectory,
                "Release",
                "Publish-WindowsPortable.ps1");
            string script = Path.Combine(releaseDirectory, "Publish-WindowsPortable.ps1");
            File.Copy(sourceScript, script);
            string differentlyCasedScratchRoot = Path.Combine(
                Path.GetTempPath(),
                "FILEORGANIZER-PUBLISH-TESTS",
                Path.GetFileName(scratchRoot).ToUpperInvariant());
            string differentlyCasedOutput = Path.Combine(
                differentlyCasedScratchRoot,
                "ARTIFACTS",
                "PORTABLE",
                "case-probe");

            VerificationProcessResult result = await RunPowerShellAsync(
                script,
                "-OutputDirectory",
                differentlyCasedOutput);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "not a git repository",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "must remain beneath",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "must be a direct child",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(scratchRoot, recursive: true);
        }
    }

    [Fact]
    public void PublishScriptScansTheStagedDesktopAssemblyAndRestoresTheEnvironment()
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Publish-WindowsPortable.ps1");
        Assert.True(File.Exists(scriptPath), $"Publish script was not copied to {scriptPath}.");
        string script = File.ReadAllText(scriptPath);

        const string publishedAssemblyBinding =
            "$publishedAppAssembly = Join-Path $stagingDirectory \"FileOrganizer.App.dll\"";
        string boundaryClassName =
            typeof(global::FileOrganizer.App.Tests.Architecture.DesktopMutationBoundaryTests)
                .FullName
            ?? throw new InvalidOperationException("Desktop boundary test type has no full name.");
        string executionBoundaryFilter =
            $"FullyQualifiedName={boundaryClassName}."
            + nameof(global::FileOrganizer.App.Tests.Architecture.DesktopMutationBoundaryTests
                .ProductionAssemblyDoesNotReferenceExecutionSurfaces);
        string mutationBoundaryFilter =
            $"FullyQualifiedName={boundaryClassName}."
            + nameof(global::FileOrganizer.App.Tests.Architecture.DesktopMutationBoundaryTests
                .ProductionAssemblyDoesNotBindDirectFilesystemMutationOrEscapeApis);
        string expectedBoundaryFilter =
            $"{executionBoundaryFilter}|{mutationBoundaryFilter}";
        Assert.Contains(publishedAssemblyBinding, script, StringComparison.Ordinal);
        Assert.Contains(
            "FILEORGANIZER_DESKTOP_ASSEMBLY_UNDER_TEST",
            script,
            StringComparison.Ordinal);
        Match boundaryFilterAssignment = Regex.Match(
            script,
            "(?s)\\$artifactBoundaryTestFilter\\s*=\\s*"
                + "\"(?<first>[^\"]+)\"\\s*\\+\\s*\"(?<second>[^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(boundaryFilterAssignment.Success);
        Assert.Equal(
            expectedBoundaryFilter,
            boundaryFilterAssignment.Groups["first"].Value
                + boundaryFilterAssignment.Groups["second"].Value);
        Assert.Contains(
            "\"--filter\", $artifactBoundaryTestFilter",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"published-app-boundary-results\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$artifactBoundaryResultName = \"PublishedDesktopMutationBoundary.trx\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--logger\", \"trx;LogFileName=$artifactBoundaryResultName\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--results-directory\", $artifactBoundaryResultsDirectory",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-Path -LiteralPath $artifactBoundaryResultPath -PathType Leaf",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']",
            script,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                "(?s)\\$artifactBoundaryResultsDirectory\\s*=\\s*Join-Path\\s*`\\s*"
                    + "\\$isolatedRoot\\s*`\\s*\"published-app-boundary-results\".*?"
                    + "\\$artifactBoundaryResultPath\\s*=\\s*Join-Path\\s*`\\s*"
                    + "\\$artifactBoundaryResultsDirectory\\s*`\\s*"
                    + "\\$artifactBoundaryResultName",
                RegexOptions.CultureInvariant),
            script);

        int publishIndex = script.IndexOf("& dotnet @publishArguments", StringComparison.Ordinal);
        int assemblyBindingIndex = script.IndexOf(
            publishedAssemblyBinding,
            StringComparison.Ordinal);
        int boundaryTestIndex = script.IndexOf(
            "& dotnet @artifactBoundaryTestArguments",
            StringComparison.Ordinal);
        int trxExistenceIndex = script.IndexOf(
            "Test-Path -LiteralPath $artifactBoundaryResultPath -PathType Leaf",
            StringComparison.Ordinal);
        int counterGateIndex = script.IndexOf(
            "if ($artifactBoundaryTotal -ne 2 -or",
            StringComparison.Ordinal);
        int packageVerifierIndex = script.IndexOf("& $isolatedVerifier", StringComparison.Ordinal);
        Assert.True(
            publishIndex >= 0
                && publishIndex < assemblyBindingIndex
                && assemblyBindingIndex < boundaryTestIndex
                && boundaryTestIndex < trxExistenceIndex
                && trxExistenceIndex < counterGateIndex
                && counterGateIndex < packageVerifierIndex,
            "The published App boundary scan and TRX validation must run after publish "
                + "and before package verification.");

        Assert.Matches(
            new Regex(
                "(?s)if\\s*\\(\\$artifactBoundaryTotal\\s*-ne\\s*2\\s*-or\\s*"
                    + "\\$artifactBoundaryExecuted\\s*-ne\\s*2\\s*-or\\s*"
                    + "\\$artifactBoundaryPassed\\s*-ne\\s*2\\s*-or\\s*"
                    + "\\$artifactBoundaryFailed\\s*-ne\\s*0\\s*\\)",
                RegexOptions.CultureInvariant),
            script);

        Assert.Matches(
            new Regex(
                "(?s)\\$previousAssemblyUnderTest\\s*=\\s*"
                    + "\\[Environment\\]::GetEnvironmentVariable\\(.*?"
                    + "try\\s*\\{.*?"
                    + "\\[Environment\\]::SetEnvironmentVariable\\(\\s*"
                    + "\\$assemblyUnderTestVariable,\\s*\\$publishedAppAssembly,\\s*"
                    + "\\[EnvironmentVariableTarget\\]::Process\\).*?"
                    + "& dotnet @artifactBoundaryTestArguments.*?"
                    + "\\}\\s*finally\\s*\\{\\s*"
                    + "\\[Environment\\]::SetEnvironmentVariable\\(\\s*"
                    + "\\$assemblyUnderTestVariable,\\s*\\$previousAssemblyUnderTest,\\s*"
                    + "\\[EnvironmentVariableTarget\\]::Process\\)",
                RegexOptions.CultureInvariant),
            script);
    }

    [Fact]
    public void VerifierUsesOrdinalFailClosedExpectedPayloadMembership()
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Test-WindowsPortablePackage.ps1");
        Assert.True(File.Exists(scriptPath), $"Verifier script was not copied to {scriptPath}.");
        string script = File.ReadAllText(scriptPath);

        Assert.Matches(
            new Regex(
                "(?s)\\$expectedPayloadPaths\\s*=\\s*"
                    + "\\[System\\.Collections\\.Generic\\.HashSet\\[string\\]\\]::new\\(\\s*"
                    + "\\[StringComparer\\]::Ordinal\\)",
                RegexOptions.CultureInvariant),
            script);
        Assert.Contains(
            "-not $expectedPayloadPaths.Contains($relativePath)",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$relativePath -notin $expectedPayloadPaths",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Array]::Sort($unexpectedPayloads, [StringComparer]::Ordinal)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublishScriptUsesAnIsolatedExactCommittedSourceTree()
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Publish-WindowsPortable.ps1");
        Assert.True(File.Exists(scriptPath), $"Publish script was not copied to {scriptPath}.");
        string script = File.ReadAllText(scriptPath);

        Assert.Contains("function Assert-ExactCommittedSourceTree", script, StringComparison.Ordinal);
        Assert.Contains(
            "$isolatedGitRepository = Join-Path $isolatedRoot \"repository.git\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$isolatedGitTemplate = Join-Path $isolatedRoot \"git-template\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$null = [IO.Directory]::CreateDirectory($isolatedGitTemplate)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.File]::WriteAllText($isolatedGitConfig, \"\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.File]::WriteAllText($isolatedGitAttributes, \"\")",
            script,
            StringComparison.Ordinal);

        string cloneBlock = GetRequiredScriptSection(
            script,
            "$cloneOutput = @(",
            "$cloneExitCode =");
        foreach (string cloneContract in new[]
                 {
                     "--no-replace-objects",
                     "clone",
                     "--bare",
                     "--no-local",
                     "\"--template=$isolatedGitTemplate\"",
                     "$repositoryRoot",
                     "$isolatedGitRepository",
                 })
        {
            Assert.Contains(cloneContract, cloneBlock, StringComparison.Ordinal);
        }

        string archiveBlock = GetRequiredScriptSection(
            script,
            "$archiveOutput = @(",
            "$archiveExitCode =");
        foreach (string archiveContract in new[]
                 {
                     "--no-replace-objects",
                     "\"--git-dir=$isolatedGitRepository\"",
                     "archive",
                     "--format=zip",
                     "\"--output=$sourceArchive\"",
                     "$headCommit",
                 })
        {
            Assert.Contains(archiveContract, archiveBlock, StringComparison.Ordinal);
        }
        Assert.DoesNotMatch(
            new Regex("-C\\s+\\$repositoryRoot", RegexOptions.CultureInvariant),
            archiveBlock);

        foreach (string contract in new[]
                 {
                     "GIT_CONFIG_NOSYSTEM = \"1\"",
                     "GIT_CONFIG_GLOBAL = $isolatedGitConfig",
                     "GIT_CONFIG_COUNT = \"0\"",
                     "GIT_CONFIG_PARAMETERS = $null",
                     "GIT_ATTR_NOSYSTEM = \"1\"",
                     "GIT_ATTR_SOURCE = $null",
                     "GIT_NO_REPLACE_OBJECTS = \"1\"",
                     "GIT_DIR = $null",
                     "GIT_WORK_TREE = $null",
                     "GIT_INDEX_FILE = $null",
                     "GIT_OBJECT_DIRECTORY = $null",
                     "GIT_ALTERNATE_OBJECT_DIRECTORIES = $null",
                     "GIT_COMMON_DIR = $null",
                     "GIT_NAMESPACE = $null",
                     "GIT_REPLACE_REF_BASE = $null",
                     "GIT_SHALLOW_FILE = $null",
                     "GIT_EXEC_PATH = $null",
                 })
        {
            Assert.Contains(contract, script, StringComparison.Ordinal);
        }
        Assert.Matches(
            new Regex(
                "(?s)foreach\\s*\\(\\$environmentName in "
                    + "\\$gitEnvironmentOverrides\\.Keys\\).*?"
                    + "\\[Environment\\]::SetEnvironmentVariable\\(\\s*"
                    + "\\$environmentName,\\s*"
                    + "\\$gitEnvironmentOverrides\\[\\$environmentName\\]",
                RegexOptions.CultureInvariant),
            script);
        Assert.Matches(
            new Regex(
                "(?s)finally\\s*\\{\\s*foreach\\s*\\(\\$environmentName in "
                    + "\\$appliedGitEnvironmentNames\\).*?"
                    + "\\$previousGitEnvironment\\[\\$environmentName\\]",
                RegexOptions.CultureInvariant),
            script);

        Assert.Contains("--no-replace-objects", script, StringComparison.Ordinal);
        Assert.Contains("--source=$Commit", script, StringComparison.Ordinal);
        Assert.Contains("export-ignore", script, StringComparison.Ordinal);
        Assert.Contains("export-subst", script, StringComparison.Ordinal);
        Assert.Contains(
            "The committed release tree cannot use export-ignore or export-subst",
            script,
            StringComparison.Ordinal);
        Assert.True(
            Regex.Count(
                script,
                "\\[System\\.Collections\\.Generic\\.Dictionary\\[string, string\\]\\]::new\\(\\s*"
                    + "\\[StringComparer\\]::Ordinal\\)",
                RegexOptions.CultureInvariant) >= 2,
            "Expected and extracted committed file sets must both use ordinal identity.");
        Assert.Contains(
            "if ($actualFiles.Count -ne $expectedFiles.Count)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (-not $actualFiles.ContainsKey($expectedEntry.Key))",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.Arguments = \"cat-file blob $ObjectId\"",
            script,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                "(?s)Write-ExactGitBlob.*?"
                    + "-ObjectId\\s+\\$expectedEntry\\.Value.*?"
                    + "hash-object\\s*`\\s*--no-filters.*?"
                    + "\\[string\\]::Equals\\(\\s*\\$actualObjectId,\\s*"
                    + "\\$expectedEntry\\.Value,\\s*\\[StringComparison\\]::Ordinal\\)",
                RegexOptions.CultureInvariant),
            script);
        Assert.Contains(
            "Assert-ExactCommittedSourceTree `",
            script,
            StringComparison.Ordinal);

        Assert.Equal(
            2,
            Regex.Count(
                script,
                "\\.Split\\(\\s*\\[char\\[\\]\\]\\s*@\\(\\[char\\]\\s*0\\),\\s*"
                    + "\\[StringSplitOptions\\]::RemoveEmptyEntries\\)",
                RegexOptions.CultureInvariant));
        Assert.DoesNotMatch(
            new Regex(
                "\\.Split\\(\\s*\\[char\\]\\s*0\\s*,",
                RegexOptions.CultureInvariant),
            script);

        Assert.Matches(
            new Regex(
                "(?s)\\$livePublisherText\\s*=\\s*"
                    + "\\[IO\\.File\\]::ReadAllText\\(\\$PSCommandPath\\).*?"
                    + "\\$committedPublisherText\\s*=\\s*"
                    + "\\[IO\\.File\\]::ReadAllText\\(\\$isolatedPublisher\\).*?"
                    + "\\[string\\]::Equals\\(\\s*\\$livePublisherText,\\s*"
                    + "\\$committedPublisherText,\\s*\\[StringComparison\\]::Ordinal\\).*?"
                    + "The running publish orchestrator differs from the exact committed script",
                RegexOptions.CultureInvariant),
            script);

        int applyEnvironmentIndex = script.IndexOf(
            "foreach ($environmentName in $gitEnvironmentOverrides.Keys)",
            StringComparison.Ordinal);
        int headIndex = script.IndexOf("$headOutput = @(", StringComparison.Ordinal);
        int cloneIndex = script.IndexOf("$cloneOutput = @(", StringComparison.Ordinal);
        int isolatedCommitIndex = script.IndexOf(
            "$isolatedCommitOutput = @(",
            StringComparison.Ordinal);
        int archiveIndex = script.IndexOf("$archiveOutput = @(", StringComparison.Ordinal);
        int expandIndex = script.IndexOf("Expand-Archive", StringComparison.Ordinal);
        int exactTreeIndex = script.IndexOf(
            "    Assert-ExactCommittedSourceTree `",
            StringComparison.Ordinal);
        int publisherComparisonIndex = script.IndexOf(
            "$livePublisherText =",
            StringComparison.Ordinal);
        int pushLocationIndex = script.IndexOf(
            "Push-Location -LiteralPath $isolatedSource",
            StringComparison.Ordinal);
        int postPublishStatusIndex = script.IndexOf(
            "$postPublishStatus = @(",
            StringComparison.Ordinal);
        int restoreEnvironmentIndex = script.LastIndexOf(
            "foreach ($environmentName in $appliedGitEnvironmentNames)",
            StringComparison.Ordinal);
        Assert.True(
            applyEnvironmentIndex >= 0
                && applyEnvironmentIndex < headIndex
                && headIndex < cloneIndex
                && cloneIndex < isolatedCommitIndex
                && isolatedCommitIndex < archiveIndex
                && archiveIndex < expandIndex
                && expandIndex < exactTreeIndex
                && exactTreeIndex < publisherComparisonIndex
                && publisherComparisonIndex < pushLocationIndex
                && pushLocationIndex < postPublishStatusIndex
                && postPublishStatusIndex < restoreEnvironmentIndex,
            "Git isolation, exact-source reconstruction, and environment restoration "
                + "must retain their fail-closed ordering.");
    }

    [Fact]
    public async Task WindowsPowerShellFiveSupportsTheExplicitNulSplitOverload()
    {
        RequireWindowsPowerShell();
        const string command = """
            $sample = "first$([char] 0)second$([char] 0)"
            $parts = $sample.Split(
                [char[]] @([char] 0),
                [StringSplitOptions]::RemoveEmptyEntries)
            if ($parts.Count -ne 2 -or
                $parts[0] -ne "first" -or
                $parts[1] -ne "second") {
                throw "Unexpected NUL split result."
            }
            Write-Output ($parts -join "|")
            """;

        VerificationProcessResult result = await RunPowerShellCommandAsync(command);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("first|second", result.Output, StringComparison.Ordinal);
    }

    private static string GetRequiredScriptSection(
        string script,
        string startMarker,
        string endMarker)
    {
        int startIndex = script.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Script section start was not found: {startMarker}");
        int endIndex = script.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Script section end was not found: {endMarker}");
        return script.Substring(startIndex, endIndex - startIndex);
    }

    private static async Task<VerificationProcessResult> RunVerifierAsync(
        string packagePath,
        string expectedCommit)
    {
        string script = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Test-WindowsPortablePackage.ps1");
        Assert.True(File.Exists(script), $"Verifier script was not copied to {script}.");

        return await RunPowerShellAsync(
            script,
            "-PackageDirectory",
            packagePath,
            "-ExpectedCommit",
            expectedCommit);
    }

    private static async Task<VerificationProcessResult> RunPowerShellAsync(
        string script,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(GetWindowsPowerShellPath())
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return new VerificationProcessResult(
            process.ExitCode,
            (await output) + Environment.NewLine + (await error));
    }

    private static async Task<VerificationProcessResult> RunPowerShellCommandAsync(
        string command)
    {
        var startInfo = new ProcessStartInfo(GetWindowsPowerShellPath())
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return new VerificationProcessResult(
            process.ExitCode,
            (await output) + Environment.NewLine + (await error));
    }

    private static void RequireWindowsPortableVerifier()
    {
        RequireWindowsPowerShell();
        Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "FileOrganizer.App.exe")),
            "The App project executable was not copied to the test output.");
        Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "FileOrganizer.App.dll")),
            "The App project assembly was not copied to the test output.");
    }

    private static void RequireWindowsPowerShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PowerShell test.");
        string powershell = GetWindowsPowerShellPath();
        Assert.SkipUnless(File.Exists(powershell), "Windows PowerShell is unavailable.");
    }

    private static string GetWindowsPowerShellPath()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }

    private sealed record VerificationProcessResult(int ExitCode, string Output);

    private sealed class PackageFixture : IDisposable
    {
        private PackageFixture(string fullPath, string expectedCommit)
        {
            FullPath = fullPath;
            ExpectedCommit = expectedCommit;
        }

        public string FullPath { get; }

        public string ExpectedCommit { get; }

        public static PackageFixture Create(bool diagnosticsEnabled)
        {
            string fullPath = Path.Combine(
                Path.GetTempPath(),
                "fileorganizer-release-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fullPath);
            foreach (string fileName in RequiredBinaryFiles.Where(
                         static name => !FirstPartyBinaryFiles.Contains(
                             name,
                             StringComparer.Ordinal)))
            {
                File.WriteAllBytes(Path.Combine(fullPath, fileName), [1]);
            }
            string resourceDirectory = Path.Combine(fullPath, "fr");
            Directory.CreateDirectory(resourceDirectory);
            File.WriteAllBytes(
                Path.Combine(resourceDirectory, "Fixture.Resources.dll"),
                [1]);

            foreach (string firstPartyBinary in FirstPartyBinaryFiles)
            {
                string sourcePath = Path.Combine(
                    AppContext.BaseDirectory,
                    firstPartyBinary);
                if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length <= 1)
                {
                    throw new InvalidOperationException(
                        $"A real first-party fixture binary is unavailable: {sourcePath}");
                }

                File.Copy(sourcePath, Path.Combine(fullPath, firstPartyBinary));
            }

            string sourceAssembly = Path.Combine(
                AppContext.BaseDirectory,
                "FileOrganizer.App.dll");
            string productVersion = FileVersionInfo.GetVersionInfo(sourceAssembly)
                .ProductVersion ?? "";
            Match commitMatch = Regex.Match(
                productVersion,
                "(?i)(?<commit>[0-9a-f]{40})",
                RegexOptions.CultureInvariant);
            if (!commitMatch.Success)
            {
                throw new InvalidOperationException(
                    $"App ProductVersion does not contain a full Git commit: {productVersion}");
            }

            string expectedCommit = commitMatch.Groups["commit"].Value.ToLowerInvariant();

            File.WriteAllText(
                Path.Combine(fullPath, "FileOrganizer.App.deps.json"),
                """
                {
                  "runtimeTarget": {
                    "name": ".NETCoreApp,Version=v10.0/win-x64"
                  },
                  "targets": {
                    ".NETCoreApp,Version=v10.0/win-x64": {
                      "FileOrganizer.App/1.0.0": {
                        "runtime": {
                          "FileOrganizer.App.dll": {}
                        }
                      },
                      "FileOrganizer.Application/1.0.0": {
                        "runtime": {
                          "FileOrganizer.Application.dll": {}
                        }
                      },
                      "FileOrganizer.Core/1.0.0": {
                        "runtime": {
                          "FileOrganizer.Core.dll": {}
                        }
                      },
                      "FileOrganizer.Infrastructure/1.0.0": {
                        "runtime": {
                          "FileOrganizer.Infrastructure.dll": {}
                        }
                      },
                      "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0": {
                        "runtime": {
                          "System.Private.CoreLib.dll": {},
                          "System.Runtime.dll": {}
                        },
                        "native": {
                          "coreclr.dll": {},
                          "clrjit.dll": {},
                          "hostfxr.dll": {},
                          "hostpolicy.dll": {}
                        }
                      },
                      "Fixture.Dependency/1.0.0": {
                        "runtime": {
                          "lib/net10.0/Fixture.Runtime.dll": {},
                          "content/Fixture.Data.dat": {}
                        },
                        "native": {
                          "runtimes/win-x64/native/Fixture.Native.dll": {},
                          "runtimes/win-x64/native/Fixture.Symbol.pdb": {}
                        },
                        "resources": {
                          "lib/net10.0/fr/Fixture.Resources.dll": {
                            "locale": "fr"
                          }
                        },
                        "runtimeTargets": {
                          "runtimes/win-x64/native/Fixture.RuntimeTarget.dll": {
                            "rid": "win-x64",
                            "assetType": "native"
                          }
                        }
                      }
                    }
                  }
                }
                """);
            File.WriteAllText(
                Path.Combine(fullPath, "FileOrganizer.App.runtimeconfig.json"),
                $$"""
                {
                  "runtimeOptions": {
                    "includedFrameworks": [
                      {
                        "name": "Microsoft.NETCore.App",
                        "version": "10.0.0"
                      }
                    ],
                    "configProperties": {
                      "Avalonia.Diagnostics.Diagnostic.IsEnabled": {{diagnosticsEnabled.ToString().ToLowerInvariant()}}
                    }
                  }
                }
                """);

            return new PackageFixture(fullPath, expectedCommit);
        }

        public void SetEntryExecutableMachine(ushort machine)
        {
            string executablePath = Path.Combine(FullPath, "FileOrganizer.App.exe");
            byte[] bytes = File.ReadAllBytes(executablePath);
            int peOffset = BitConverter.ToInt32(bytes, 0x3c);
            byte[] machineBytes = BitConverter.GetBytes(machine);
            machineBytes.CopyTo(bytes, peOffset + 4);
            File.WriteAllBytes(executablePath, bytes);
        }

        public void SetDiagnosticsJsonLiteral(string jsonLiteral)
        {
            string runtimeConfigPath = Path.Combine(
                FullPath,
                "FileOrganizer.App.runtimeconfig.json");
            string text = File.ReadAllText(runtimeConfigPath);
            const string disabledProperty =
                "\"Avalonia.Diagnostics.Diagnostic.IsEnabled\": false";
            if (!text.Contains(disabledProperty, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Runtime configuration fixture is not explicitly disabled.");
            }

            File.WriteAllText(
                runtimeConfigPath,
                text.Replace(
                    disabledProperty,
                    $"\"Avalonia.Diagnostics.Diagnostic.IsEnabled\": {jsonLiteral}",
                    StringComparison.Ordinal));
        }

        public void ReplaceDependencyAssetPath(string originalPath, string replacementPath)
        {
            string dependencyPath = Path.Combine(FullPath, "FileOrganizer.App.deps.json");
            string text = File.ReadAllText(dependencyPath);
            string originalToken = $"\"{originalPath}\": {{}}";
            string replacementToken = $"\"{replacementPath}\": {{}}";
            if (!text.Contains(originalToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dependency fixture does not contain asset {originalPath}.");
            }

            File.WriteAllText(
                dependencyPath,
                text.Replace(originalToken, replacementToken, StringComparison.Ordinal));
        }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
    }
}

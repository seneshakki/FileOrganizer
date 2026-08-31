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

    private static readonly string[] RequiredLegalFiles =
    [
        "LICENSE.txt",
        "THIRD-PARTY-NOTICES.txt",
    ];

    [Fact]
    public async Task CompleteSelfContainedDependencyFixturePassesVerification()
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("SelfContained", result.Output, StringComparison.Ordinal);
        Assert.Contains("DiagnosticsEnabled", result.Output, StringComparison.Ordinal);
        Assert.Contains(package.ExpectedCommit, result.Output, StringComparison.Ordinal);
        Assert.Contains("False", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseAssetScriptCreatesRootedArchiveChecksumAndReverifiesPayload()
    {
        RequireWindowsPowerShell();
        string artifactsRoot = Path.Combine(AppContext.BaseDirectory, "artifacts");
        string portableRoot = Path.Combine(artifactsRoot, "portable");
        string releaseRoot = Path.Combine(artifactsRoot, "release");
        Assert.False(Directory.Exists(releaseRoot), $"Release fixture root already exists: {releaseRoot}");
        Directory.CreateDirectory(portableRoot);

        using PackageFixture package = PackageFixture.Create(
            diagnosticsEnabled: false,
            packageParent: portableRoot);
        string productVersion = FileVersionInfo.GetVersionInfo(
            Path.Combine(package.FullPath, "FileOrganizer.App.exe")).ProductVersion ?? "";
        int metadataSeparator = productVersion.LastIndexOf('+');
        Assert.True(metadataSeparator > 0, $"Fixture ProductVersion is invalid: {productVersion}");
        string version = productVersion[..metadataSeparator];
        string script = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "New-WindowsPortableReleaseAssets.ps1");

        try
        {
            VerificationProcessResult result = await RunPowerShellAsync(
                script,
                "-PackageDirectory",
                package.FullPath,
                "-Version",
                version,
                "-ExpectedCommit",
                package.ExpectedCommit);

            Assert.True(result.ExitCode == 0, result.Output);
            string payloadName = $"FileOrganizer-{version}-win-x64-portable";
            string bundlePath = Path.Combine(releaseRoot, $"{payloadName}-assets");
            string archivePath = Path.Combine(bundlePath, $"{payloadName}.zip");
            string checksumPath = $"{archivePath}.sha256";
            Assert.True(Directory.Exists(bundlePath));
            Assert.True(File.Exists(archivePath));
            Assert.True(File.Exists(checksumPath));
            string expectedChecksum = File.ReadAllText(checksumPath).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)[0];
            string actualChecksum = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)));
            Assert.Equal(actualChecksum, expectedChecksum);

            using System.IO.Compression.ZipArchive archive =
                System.IO.Compression.ZipFile.OpenRead(archivePath);
            Assert.NotEmpty(archive.Entries);
            Assert.All(
                archive.Entries,
                entry => Assert.StartsWith(
                    $"{payloadName}/",
                    entry.FullName.Replace('\\', '/'),
                    StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(releaseRoot))
            {
                Directory.Delete(releaseRoot, recursive: true);
            }
            if (Directory.Exists(portableRoot)
                && !Directory.EnumerateFileSystemEntries(portableRoot).Any())
            {
                Directory.Delete(portableRoot);
            }
            if (Directory.Exists(artifactsRoot)
                && !Directory.EnumerateFileSystemEntries(artifactsRoot).Any())
            {
                Directory.Delete(artifactsRoot);
            }
        }
    }

    [Fact]
    public async Task ReleaseAssetScriptRejectsNonCanonicalNumericPrereleaseVersion()
    {
        RequireWindowsPowerShell();
        string script = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "New-WindowsPortableReleaseAssets.ps1");

        VerificationProcessResult result = await RunPowerShellAsync(
            script,
            "-PackageDirectory",
            AppContext.BaseDirectory,
            "-Version",
            "0.1.0-01",
            "-ExpectedCommit",
            new string('a', 40));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "canonical semantic version",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseAssetScriptPreservesExistingPublishedBundle()
    {
        RequireWindowsPowerShell();
        string artifactsRoot = Path.Combine(AppContext.BaseDirectory, "artifacts");
        string portableRoot = Path.Combine(artifactsRoot, "portable");
        string releaseRoot = Path.Combine(artifactsRoot, "release");
        Assert.False(Directory.Exists(releaseRoot), $"Release fixture root already exists: {releaseRoot}");
        Directory.CreateDirectory(portableRoot);

        using PackageFixture package = PackageFixture.Create(
            diagnosticsEnabled: false,
            packageParent: portableRoot);
        string productVersion = FileVersionInfo.GetVersionInfo(
            Path.Combine(package.FullPath, "FileOrganizer.App.exe")).ProductVersion ?? "";
        int metadataSeparator = productVersion.LastIndexOf('+');
        Assert.True(metadataSeparator > 0, $"Fixture ProductVersion is invalid: {productVersion}");
        string version = productVersion[..metadataSeparator];
        string payloadName = $"FileOrganizer-{version}-win-x64-portable";
        string bundlePath = Path.Combine(releaseRoot, $"{payloadName}-assets");
        Directory.CreateDirectory(bundlePath);
        string sentinel = Path.Combine(bundlePath, "existing.txt");
        File.WriteAllText(sentinel, "preserve");
        string script = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "New-WindowsPortableReleaseAssets.ps1");

        try
        {
            VerificationProcessResult result = await RunPowerShellAsync(
                script,
                "-PackageDirectory",
                package.FullPath,
                "-Version",
                version,
                "-ExpectedCommit",
                package.ExpectedCommit);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "release asset bundle already exists",
                result.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("preserve", File.ReadAllText(sentinel));
            Assert.Equal([sentinel], Directory.EnumerateFiles(bundlePath));
        }
        finally
        {
            if (Directory.Exists(releaseRoot))
            {
                Directory.Delete(releaseRoot, recursive: true);
            }
            if (Directory.Exists(portableRoot)
                && !Directory.EnumerateFileSystemEntries(portableRoot).Any())
            {
                Directory.Delete(portableRoot);
            }
            if (Directory.Exists(artifactsRoot)
                && !Directory.EnumerateFileSystemEntries(artifactsRoot).Any())
            {
                Directory.Delete(artifactsRoot);
            }
        }
    }

    [Fact]
    public async Task ReleaseAssetScriptRejectsReparseAncestorWithoutWritingThroughIt()
    {
        RequireWindowsPowerShell();
        string artifactsRoot = Path.Combine(AppContext.BaseDirectory, "artifacts");
        string portableRoot = Path.Combine(artifactsRoot, "portable");
        string releaseRoot = Path.Combine(artifactsRoot, "release");
        Directory.CreateDirectory(artifactsRoot);
        Assert.False(Directory.Exists(releaseRoot), $"Release fixture root already exists: {releaseRoot}");
        if (Directory.Exists(portableRoot))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(portableRoot));
            Assert.False(
                (File.GetAttributes(portableRoot) & FileAttributes.ReparsePoint) != 0,
                $"Portable fixture root is unexpectedly a reparse point: {portableRoot}");
            Directory.Delete(portableRoot);
        }
        string externalRoot = Path.Combine(
            Path.GetTempPath(),
            $"fileorganizer-release-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalRoot);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(portableRoot, externalRoot);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Skip(
                    $"Symbolic links are unavailable in this test environment: {exception.GetType().Name}");
            }

            using PackageFixture package = PackageFixture.Create(
                diagnosticsEnabled: false,
                packageParent: portableRoot);
            string productVersion = FileVersionInfo.GetVersionInfo(
                Path.Combine(package.FullPath, "FileOrganizer.App.exe")).ProductVersion ?? "";
            int metadataSeparator = productVersion.LastIndexOf('+');
            Assert.True(metadataSeparator > 0, $"Fixture ProductVersion is invalid: {productVersion}");
            string version = productVersion[..metadataSeparator];
            string script = Path.Combine(
                AppContext.BaseDirectory,
                "Release",
                "New-WindowsPortableReleaseAssets.ps1");

            VerificationProcessResult result = await RunPowerShellAsync(
                script,
                "-PackageDirectory",
                package.FullPath,
                "-Version",
                version,
                "-ExpectedCommit",
                package.ExpectedCommit);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(releaseRoot));
        }
        finally
        {
            if (Directory.Exists(portableRoot))
            {
                Directory.Delete(portableRoot);
            }
            if (Directory.Exists(externalRoot))
            {
                Directory.Delete(externalRoot, recursive: true);
            }
            if (Directory.Exists(artifactsRoot)
                && !Directory.EnumerateFileSystemEntries(artifactsRoot).Any())
            {
                Directory.Delete(artifactsRoot);
            }
        }
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
    [MemberData(nameof(RequiredLegalPayloads))]
    public async Task MissingRequiredLegalPayloadIsRejected(string fileName)
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        File.Delete(Path.Combine(package.FullPath, fileName));

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing required file", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fileName, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RequiredLegalPayloads))]
    public async Task ModifiedLegalPayloadIsRejected(string fileName)
    {
        RequireWindowsPortableVerifier();
        using PackageFixture package = PackageFixture.Create(diagnosticsEnabled: false);
        File.AppendAllText(Path.Combine(package.FullPath, fileName), "tampered");

        VerificationProcessResult result = await RunVerifierAsync(
            package.FullPath,
            package.ExpectedCommit);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "does not match the committed source",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fileName, result.Output, StringComparison.Ordinal);
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
                    + "Set-ProcessEnvironmentVariable\\s*`\\s*"
                    + "-Name\\s+\\$assemblyUnderTestVariable\\s*`\\s*"
                    + "-Value\\s+\\$publishedAppAssembly.*?"
                    + "& dotnet @artifactBoundaryTestArguments.*?"
                    + "\\}\\s*finally\\s*\\{\\s*"
                    + "Set-ProcessEnvironmentVariable\\s*`\\s*"
                    + "-Name\\s+\\$assemblyUnderTestVariable\\s*`\\s*"
                    + "-Value\\s+\\$previousAssemblyUnderTest",
                RegexOptions.CultureInvariant),
            script);
    }

    [Fact]
    public void PublishScriptFailsClosedOnNuGetAuditSchemaAndExactProjectSet()
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Publish-WindowsPortable.ps1");
        Assert.True(File.Exists(scriptPath), $"Publish script was not copied to {scriptPath}.");
        string script = File.ReadAllText(scriptPath);

        Assert.Contains(
            "ConvertFrom-Json -InputObject $auditText",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int] $auditDocument.version -ne 1",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$actualAuditProjects.Count -ne $expectedAuditProjects.Count",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$auditText -match '\"vulnerabilities\"\\s*:'",
            script,
            StringComparison.Ordinal);

        string[] expectedProjects =
        [
            "FileOrganizer.App.csproj",
            "FileOrganizer.Application.csproj",
            "FileOrganizer.Core.csproj",
            "FileOrganizer.Infrastructure.csproj",
            "FileOrganizer.App.Tests.csproj",
            "FileOrganizer.Application.Tests.csproj",
            "FileOrganizer.Core.Tests.csproj",
            "FileOrganizer.Infrastructure.LeaseProbe.csproj",
            "FileOrganizer.Infrastructure.Tests.csproj",
        ];
        Assert.All(
            expectedProjects,
            project => Assert.Contains(project, script, StringComparison.Ordinal));

        int restoreIndex = script.IndexOf(
            "& dotnet @solutionRestoreArguments",
            StringComparison.Ordinal);
        int auditIndex = script.IndexOf(
            "$auditOutput = @(& dotnet @auditArguments)",
            StringComparison.Ordinal);
        int buildIndex = script.IndexOf("& dotnet @buildArguments", StringComparison.Ordinal);
        Assert.True(
            restoreIndex >= 0 && restoreIndex < auditIndex && auditIndex < buildIndex,
            "The validated vulnerability audit must run after locked restore and before build.");
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
                    + "Set-ProcessEnvironmentVariable\\s*`\\s*"
                    + "-Name\\s+\\$environmentName\\s*`\\s*"
                    + "-Value\\s+\\$gitEnvironmentOverrides\\[\\$environmentName\\]",
                RegexOptions.CultureInvariant),
            script);
        Assert.Matches(
            new Regex(
                "(?s)finally\\s*\\{\\s*foreach\\s*\\(\\$environmentName in "
                    + "\\$appliedGitEnvironmentNames\\).*?"
                    + "Set-ProcessEnvironmentVariable\\s*`\\s*"
                    + "-Name\\s+\\$environmentName\\s*`\\s*"
                    + "-Value\\s+\\$previousGitEnvironment\\[\\$environmentName\\]",
                RegexOptions.CultureInvariant),
            script);
        Assert.Contains(
            "[Management.Automation.Language.NullString]::Value",
            script,
            StringComparison.Ordinal);

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
    public void PublishScriptRetriesOnlyRevalidatedGeneratedDirectoryCleanup()
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Publish-WindowsPortable.ps1");
        Assert.True(File.Exists(scriptPath), $"Publish script was not copied to {scriptPath}.");
        string script = File.ReadAllText(scriptPath);
        string cleanupHelper = GetRequiredScriptSection(
            script,
            "function Remove-ValidatedDirectoryTreeWithRetry",
            "function Set-ProcessEnvironmentVariable");

        foreach (string contract in new[]
                 {
                     "for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++)",
                     "$cleanupParent.FullName",
                     "$cleanupName -notmatch $ExpectedNamePattern",
                     "$currentBoundaryPath = $expectedParentPath",
                     "$currentBoundaryPath,",
                     "$trustedBoundaryPath,",
                     "$boundaryEntry.PSIsContainer",
                     "[IO.FileAttributes]::ReparsePoint",
                     "Remove-Item -LiteralPath $cleanupTarget -Recurse -Force",
                     "Test-RetryableDirectoryCleanupError $removeError",
                     "& $DelayAction $RetryDelayMilliseconds",
                 })
        {
            Assert.Contains(contract, cleanupHelper, StringComparison.Ordinal);
        }

        Assert.Contains("[IO.IOException]", script, StringComparison.Ordinal);
        Assert.Contains("$win32Error -eq 32 -or $win32Error -eq 33", script, StringComparison.Ordinal);
        Assert.Contains("[int] $MaximumAttempts = 6", cleanupHelper, StringComparison.Ordinal);
        Assert.Contains("[int] $RetryDelayMilliseconds = 200", cleanupHelper, StringComparison.Ordinal);
        Assert.Equal(
            3,
            Regex.Count(
                script,
                "Remove-ValidatedDirectoryTreeWithRetry",
                RegexOptions.CultureInvariant));
        Assert.Contains(
            "-ExpectedNamePattern '^\\.FileOrganizer-staging-[0-9a-f]{32}$'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedNamePattern '^FileOrganizer-release-[0-9a-f]{32}$'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($locationPushed)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishScriptRetriesARealWindowsSharingViolation()
    {
        RequireWindowsPowerShell();
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Release",
            "Publish-WindowsPortable.ps1");
        Assert.True(File.Exists(scriptPath), $"Publish script was not copied to {scriptPath}.");
        string escapedScriptPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            $ErrorActionPreference = "Stop"
            $publishScript = '{{escapedScriptPath}}'
            $scriptDirectory = [IO.Directory]::GetParent($publishScript).FullName
            $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory ".."))
            $invalidOutput = Join-Path $repositoryRoot "artifacts\portable"
            try {
                . $publishScript -OutputDirectory $invalidOutput
                throw "The publish script unexpectedly continued."
            }
            catch {
                if ($_.Exception.Message -notlike "*must remain beneath*") {
                    throw
                }
            }

            $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
            $target = Join-Path $temporaryRoot (
                "FileOrganizer-release-" + [Guid]::NewGuid().ToString("N"))
            $lockedStream = $null
            try {
                $null = [IO.Directory]::CreateDirectory($target)
                $lockedFile = Join-Path $target "locked.bin"
                $lockedStream = [IO.File]::Open(
                    $lockedFile,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::ReadWrite,
                    [IO.FileShare]::Read)
                $delayCalls = 0
                $releaseLock = {
                    param([int] $Milliseconds)
                    $script:delayCalls++
                    $script:lockedStream.Dispose()
                    $script:lockedStream = $null
                }

                Remove-ValidatedDirectoryTreeWithRetry `
                    -Path $target `
                    -ExpectedParent $temporaryRoot `
                    -TrustedBoundary $temporaryRoot `
                    -ExpectedNamePattern '^FileOrganizer-release-[0-9a-f]{32}$' `
                    -TargetDescription "release retry test workspace" `
                    -MaximumAttempts 3 `
                    -RetryDelayMilliseconds 0 `
                    -DelayAction $releaseLock

                if ($delayCalls -ne 1) {
                    throw "Expected one retry delay, observed $delayCalls."
                }
                if (Test-Path -LiteralPath $target) {
                    throw "The retry did not remove the generated test workspace."
                }
                Write-Output "sharing-violation-retry-ok"
            }
            finally {
                if ($null -ne $lockedStream) {
                    $lockedStream.Dispose()
                }
                if (Test-Path -LiteralPath $target) {
                    [IO.Directory]::Delete($target, $true)
                }
            }
            """;

        VerificationProcessResult result = await RunPowerShellCommandAsync(command);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sharing-violation-retry-ok", result.Output, StringComparison.Ordinal);
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

    public static TheoryData<string> RequiredLegalPayloads => new()
    {
        "LICENSE.txt",
        "THIRD-PARTY-NOTICES.txt",
    };

    private sealed record VerificationProcessResult(int ExitCode, string Output);

    private sealed class PackageFixture : IDisposable
    {
        private readonly string? _emptyParentToDelete;

        private PackageFixture(
            string fullPath,
            string expectedCommit,
            string? emptyParentToDelete)
        {
            FullPath = fullPath;
            ExpectedCommit = expectedCommit;
            _emptyParentToDelete = emptyParentToDelete;
        }

        public string FullPath { get; }

        public string ExpectedCommit { get; }

        public static PackageFixture Create(
            bool diagnosticsEnabled,
            string? packageParent = null)
        {
            string? emptyParentToDelete = packageParent is null
                ? Path.Combine(Path.GetTempPath(), "fileorganizer-release-tests")
                : null;
            packageParent ??= emptyParentToDelete;
            string fullPath = Path.Combine(
                packageParent!,
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

            foreach (string legalFile in RequiredLegalFiles)
            {
                string sourceName = string.Equals(
                    legalFile,
                    "LICENSE.txt",
                    StringComparison.Ordinal)
                    ? "LICENSE"
                    : legalFile;
                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, sourceName),
                    Path.Combine(fullPath, legalFile));
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

            return new PackageFixture(fullPath, expectedCommit, emptyParentToDelete);
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
            if (_emptyParentToDelete is not null
                && Directory.Exists(_emptyParentToDelete)
                && !Directory.EnumerateFileSystemEntries(_emptyParentToDelete).Any())
            {
                try
                {
                    Directory.Delete(_emptyParentToDelete);
                }
                catch (IOException)
                {
                    // Another test process may have populated the shared temp parent.
                }
            }
        }
    }
}

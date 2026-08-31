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
    public async Task UnexpectedExecutableOrAssemblyPayloadIsRejected(
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
            "Unexpected executable or assembly payloads",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(relativePath, result.Output, StringComparison.OrdinalIgnoreCase);
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
                          "lib/net10.0/Fixture.Runtime.dll": {}
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

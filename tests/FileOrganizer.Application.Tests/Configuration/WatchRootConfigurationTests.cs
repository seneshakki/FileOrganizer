using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Tests.Configuration;

public sealed class WatchRootConfigurationTests
{
    private static readonly Guid FirstId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SecondId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void WatchRootsRoundTripInConfiguredOrderAndAreWrittenExplicitly()
    {
        PathPolicy policy = WindowsPolicy();
        OrganizationConfiguration original = new(
            policy.Semantics,
            [],
            [
                new WatchRootConfiguration(
                    FirstId,
                    Canonical(policy, @"C:\Nonexistent\Gelen"),
                    isEnabled: true,
                    includeSubdirectories: true),
                new WatchRootConfiguration(
                    SecondId,
                    Canonical(policy, "D:\\Ar\u015fiv"),
                    isEnabled: false,
                    includeSubdirectories: false),
            ]);

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(original);
        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(encoded.Json!);
        ConfigurationEncodeResult encodedAgain = OrganizationConfigurationJsonCodec.Encode(
            decoded.Configuration!);

        Assert.True(encoded.Succeeded);
        Assert.True(decoded.Succeeded);
        Assert.Contains("\"watchRoots\": [", encoded.Json, StringComparison.Ordinal);
        Assert.Equal(encoded.Json, encodedAgain.Json);
        Assert.Collection(
            decoded.Configuration!.WatchRoots,
            first =>
            {
                Assert.Equal(FirstId, first.Id);
                Assert.Equal(@"C:\Nonexistent\Gelen", first.RootPath.Value);
                Assert.True(first.IsEnabled);
                Assert.True(first.IncludeSubdirectories);
            },
            second =>
            {
                Assert.Equal(SecondId, second.Id);
                Assert.Equal("D:\\Ar\u015fiv", second.RootPath.Value);
                Assert.False(second.IsEnabled);
                Assert.False(second.IncludeSubdirectories);
            });
    }

    [Fact]
    public void OldVersionOneDocumentWithoutWatchRootsLoadsAnEmptyList()
    {
        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(
            EmptyVersionOneJsonWithoutWatchRoots());

        Assert.True(decoded.Succeeded);
        Assert.Empty(decoded.Configuration!.WatchRoots);

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(
            decoded.Configuration);
        Assert.Contains("\"watchRoots\": []", encoded.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateWatchRootIdsAreRejectedAcrossEnabledStates()
    {
        string json = ConfigurationJson(
            WatchRootJson(FirstId, @"C:\Inbox", enabled: true),
            WatchRootJson(FirstId, @"D:\Inbox", enabled: false));

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(json);

        ConfigurationProblem problem = Assert.Single(
            decoded.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.DuplicateWatchRootId);
        Assert.Equal("/watchRoots/1/id", problem.Location);
        Assert.Null(decoded.Configuration);
    }

    [Fact]
    public void LogicallyEquivalentWatchRootPathsAreRejectedAcrossCaseAndUnicodeSpelling()
    {
        string composed = "Donn\u00e9es";
        string decomposed = "Donne\u0301es";
        string json = ConfigurationJson(
            WatchRootJson(FirstId, @"C:\Data\" + composed, enabled: true),
            WatchRootJson(SecondId, @"c:\data\" + decomposed, enabled: false));

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(json);

        ConfigurationProblem problem = Assert.Single(
            decoded.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.DuplicateWatchRootPath);
        Assert.Equal("/watchRoots/1/rootPath", problem.Location);
        Assert.Null(decoded.Configuration);
    }

    [Theory]
    [InlineData("relative\\inbox")]
    [InlineData("C:\\Inbox\\..\\Inbox")]
    public void InvalidOrNonCanonicalWatchRootPathIsRejected(string rootPath)
    {
        string json = ConfigurationJson(WatchRootJson(FirstId, rootPath, enabled: true));

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(json);

        ConfigurationProblem problem = Assert.Single(
            decoded.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidPath);
        Assert.Equal("/watchRoots/0/rootPath", problem.Location);
        Assert.Null(decoded.Configuration);
    }

    [Fact]
    public void WatchRootPathContainingNullIsRejected()
    {
        string json = ConfigurationJson(
            WatchRootJson(FirstId, "C:\\bad\0path", enabled: true));

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(json);

        ConfigurationProblem problem = Assert.Single(
            decoded.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidPath);
        Assert.Equal("/watchRoots/0/rootPath", problem.Location);
        Assert.Null(decoded.Configuration);
    }

    [Fact]
    public void MalformedWatchRootsDoNotBecomeConfiguration()
    {
        string json = EmptyVersionOneJsonWithoutWatchRoots().Replace(
            "\"rules\": []",
            "\"watchRoots\": {}, \"rules\": []",
            StringComparison.Ordinal);

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(json);

        ConfigurationProblem problem = Assert.Single(
            decoded.Problems,
            static problem => problem.Location == "/watchRoots");
        Assert.Equal(ConfigurationProblemKind.InvalidValue, problem.Kind);
        Assert.Null(decoded.Configuration);
    }

    [Fact]
    public void MissingWatchRootFieldIsRejected()
    {
        string json = ConfigurationJson(
            $$"""
            {
              "id": "{{FirstId:D}}",
              "rootPath": "C:\\Inbox",
              "enabled": true
            }
            """);

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.Contains(
            decoded.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.MissingRequiredField
                && problem.Location == "/watchRoots/0/includeSubdirectories");
        Assert.Null(decoded.Configuration);
    }

    [Fact]
    public void EmptyWatchRootIdIsRejected()
    {
        string json = ConfigurationJson(
            WatchRootJson(Guid.Empty, @"C:\Inbox", enabled: true));

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.Contains(
            decoded.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidValue
                && problem.Location == "/watchRoots/0/id");
        Assert.Null(decoded.Configuration);
    }

    [Fact]
    public void ConfigurationSnapshotsCallerOwnedWatchRootCollections()
    {
        PathPolicy policy = WindowsPolicy();
        List<WatchRootConfiguration> watchRoots =
        [
            new(
                FirstId,
                Canonical(policy, @"Z:\This\Directory\NeedNot\Exist"),
                isEnabled: true,
                includeSubdirectories: false),
        ];
        OrganizationConfiguration configuration = new(policy.Semantics, [], watchRoots);

        watchRoots.Clear();

        Assert.Single(configuration.WatchRoots);
        Assert.Throws<NotSupportedException>(
            () => ((IList<WatchRootConfiguration>)configuration.WatchRoots).Clear());
    }

    [Fact]
    public void RuntimeModelRejectsDuplicateLogicalRoots()
    {
        PathPolicy policy = WindowsPolicy();
        WatchRootConfiguration first = new(
            FirstId,
            Canonical(policy, @"C:\Data\Inbox"),
            isEnabled: true,
            includeSubdirectories: false);
        WatchRootConfiguration second = new(
            SecondId,
            Canonical(policy, @"c:\data\INBOX"),
            isEnabled: false,
            includeSubdirectories: true);

        Assert.Throws<ArgumentException>(
            () => new OrganizationConfiguration(policy.Semantics, [], [first, second]));
    }

    private static PathPolicy WindowsPolicy() =>
        new(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.FormC);

    private static CanonicalPath Canonical(PathPolicy policy, string value)
    {
        PathResolution resolution = policy.CanonicalizeAbsolute(value);
        Assert.True(resolution.Succeeded, resolution.Problem?.Description);
        return resolution.Path!;
    }

    private static string ConfigurationJson(params string[] watchRoots) => $$"""
        {
          "schemaVersion": 1,
          "pathPolicy": {
            "syntax": "windows",
            "caseSensitivity": "insensitive",
            "unicodeNormalization": "formC"
          },
          "watchRoots": [{{string.Join(",", watchRoots)}}],
          "rules": []
        }
        """;

    private static string WatchRootJson(Guid id, string rootPath, bool enabled) => $$"""
        {
          "id": "{{id:D}}",
          "rootPath": {{System.Text.Json.JsonSerializer.Serialize(rootPath)}},
          "enabled": {{enabled.ToString().ToLowerInvariant()}},
          "includeSubdirectories": true
        }
        """;

    private static string EmptyVersionOneJsonWithoutWatchRoots() =>
        """
        {
          "schemaVersion": 1,
          "pathPolicy": {
            "syntax": "windows",
            "caseSensitivity": "insensitive",
            "unicodeNormalization": "formC"
          },
          "rules": []
        }
        """;
}

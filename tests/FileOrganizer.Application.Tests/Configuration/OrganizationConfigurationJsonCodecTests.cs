using System.Globalization;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Matching.Logical;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Application.Tests.Configuration;

public sealed class OrganizationConfigurationJsonCodecTests
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void FullRuleSetRoundTripsEveryConditionAndActionInStableOrder()
    {
        OrganizationConfiguration original = FullConfiguration();

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(original);
        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(encoded.Json!);
        ConfigurationEncodeResult encodedAgain = OrganizationConfigurationJsonCodec.Encode(
            decoded.Configuration!);

        Assert.True(encoded.Succeeded);
        Assert.True(decoded.Succeeded);
        Assert.Equal(encoded.Json, encodedAgain.Json);
        Assert.Contains("Görseller", encoded.Json, StringComparison.Ordinal);
        Assert.Contains("C:\\\\Arşiv\\\\Görseller", encoded.Json, StringComparison.Ordinal);

        OrganizationConfiguration restored = decoded.Configuration!;
        Assert.Equal(PathSyntax.Windows, restored.PathSemantics.Syntax);
        Assert.Equal(PathCaseSensitivity.Insensitive, restored.PathSemantics.CaseSensitivity);
        Assert.Equal(PathUnicodeNormalization.FormC, restored.PathSemantics.UnicodeNormalization);
        Assert.Equal([SecondId, FirstId, ThirdId], restored.Rules.Select(static rule => rule.Id));
        Assert.False(restored.Rules[1].Rule.Rule.IsEnabled);

        AndCondition include = Assert.IsType<AndCondition>(restored.Rules[1].Rule.Rule.Include);
        ExtensionCondition extension = Assert.IsType<ExtensionCondition>(include.Children[0]);
        Assert.Equal([".jpg", ".png"], extension.Extensions);
        Assert.Equal(CaseSensitivity.Sensitive, extension.CaseSensitivity);

        NameCondition name = Assert.IsType<NameCondition>(include.Children[1]);
        Assert.Equal(NameTarget.WithoutExtension, name.Target);
        Assert.Equal(TextOperator.StartsWith, name.Operator);
        Assert.Equal("tatil-", name.Value);
        Assert.Equal(CaseSensitivity.Insensitive, name.CaseSensitivity);

        GlobCondition glob = Assert.IsType<GlobCondition>(include.Children[2]);
        Assert.Equal("*.tar.gz", glob.Pattern);
        Assert.Equal(NameTarget.FullName, glob.Target);
        Assert.Equal(CaseSensitivity.Sensitive, glob.CaseSensitivity);

        RegexCondition regex = Assert.IsType<RegexCondition>(include.Children[3]);
        Assert.Equal("^IMG_[0-9]+\\.jpg$", regex.Pattern);
        Assert.Equal(TimeSpan.FromMilliseconds(250), regex.Timeout);

        SizeCondition size = Assert.IsType<SizeCondition>(include.Children[4]);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqualTo, size.Operator);
        Assert.Equal(4096, size.SizeInBytes);

        TimestampCondition timestamp = Assert.IsType<TimestampCondition>(include.Children[5]);
        Assert.Equal(TimestampField.Modified, timestamp.Field);
        Assert.Equal(ComparisonOperator.LessThan, timestamp.Operator);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-02T03:04:05+00:00", CultureInfo.InvariantCulture),
            timestamp.ThresholdUtc);

        AgeCondition age = Assert.IsType<AgeCondition>(include.Children[6]);
        Assert.Equal(TimestampField.Created, age.Field);
        Assert.Equal(ComparisonOperator.GreaterThan, age.Operator);
        Assert.Equal(TimeSpan.FromDays(30), age.Age);

        OrCondition or = Assert.IsType<OrCondition>(include.Children[7]);
        Assert.Collection(
            or.Children,
            static child => Assert.IsType<NameCondition>(child),
            static child => Assert.IsType<NotCondition>(child));

        Assert.IsType<MoveToDirectoryAction>(restored.Rules[0].Rule.Action);
        Assert.IsType<CopyToDirectoryAction>(restored.Rules[1].Rule.Action);
        Assert.IsType<RenameToLiteralFileNameAction>(restored.Rules[2].Rule.Action);
    }

    [Fact]
    public void ConfigurationSnapshotsCallerOwnedRuleCollections()
    {
        List<ConfiguredOrganizationRule> rules = [Rule(FirstId, 0)];
        OrganizationConfiguration configuration = new(WindowsSemantics(), rules);

        rules.Clear();

        Assert.Single(configuration.Rules);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ConfiguredOrganizationRule>)configuration.Rules).Clear());
    }

    [Fact]
    public void LoadedConfigurationDoesNotExposeMutableCollections()
    {
        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(
            FullConfiguration());
        OrganizationConfiguration loaded = OrganizationConfigurationJsonCodec.Decode(encoded.Json!)
            .Configuration!;

        Assert.Throws<NotSupportedException>(
            () => ((IList<ConfiguredOrganizationRule>)loaded.Rules).RemoveAt(0));

        AndCondition include = Assert.IsType<AndCondition>(loaded.Rules[1].Rule.Rule.Include);
        Assert.Throws<NotSupportedException>(() => ((IList<ICondition>)include.Children).Clear());
        ExtensionCondition extension = Assert.IsType<ExtensionCondition>(include.Children[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)extension.Extensions).Clear());
    }

    [Fact]
    public void MalformedJsonIsRejectedStructurally()
    {
        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode("{ nope");

        ConfigurationProblem problem = Assert.Single(result.Problems);
        Assert.Equal(ConfigurationProblemKind.MalformedJson, problem.Kind);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void UnsupportedSchemaVersionStopsBeforeRuleMapping()
    {
        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(
            """
            {
              "schemaVersion": 2,
              "pathPolicy": null,
              "rules": "not an array"
            }
            """);

        ConfigurationProblem problem = Assert.Single(result.Problems);
        Assert.Equal(ConfigurationProblemKind.UnsupportedSchemaVersion, problem.Kind);
        Assert.Equal("/schemaVersion", problem.Location);
    }

    [Fact]
    public void UnknownEnumTokenIsRejectedStructurally()
    {
        string json = MinimalJson(
            """{"type":"name","target":"fullName","operator":"contains","value":"x","caseSensitivity":"hostDefault"}""",
            MoveActionJson());

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        ConfigurationProblem problem = Assert.Single(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidValue);
        Assert.Equal("/rules/0/include/caseSensitivity", problem.Location);
        Assert.Null(result.Configuration);
    }

    [Theory]
    [InlineData("mystery", ConfigurationProblemKind.UnknownConditionDiscriminator)]
    [InlineData("regex", ConfigurationProblemKind.InvalidCondition)]
    [InlineData("glob", ConfigurationProblemKind.InvalidCondition)]
    public void InvalidConditionConfigurationNeverProducesRuntimeConfiguration(
        string conditionType,
        ConfigurationProblemKind expectedKind)
    {
        string condition = conditionType switch
        {
            "regex" => """{"type":"regex","target":"fullName","pattern":"[","caseSensitivity":"insensitive","timeout":"00:00:00.1000000"}""",
            "glob" => """{"type":"glob","target":"fullName","pattern":" ","caseSensitivity":"insensitive"}""",
            _ => """{"type":"mystery"}""",
        };
        string json = MinimalJson(condition, MoveActionJson());

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, problem => problem.Kind == expectedKind);
        Assert.Null(result.Configuration);
    }

    [Fact]
    public void EmptyLogicalTreeAndUnknownActionRemainDistinctProblems()
    {
        string json = MinimalJson(
            """{"type":"and","conditions":[]}""",
            """{"type":"delete"}""");

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.ImpossibleLogicalTree);
        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.UnknownActionDiscriminator);
    }

    [Fact]
    public void MissingFieldsAndInvalidRuleValuesRemainDistinctProblems()
    {
        string json = """
            {
              "schemaVersion": 1,
              "pathPolicy": {
                "syntax": "windows",
                "caseSensitivity": "insensitive",
                "unicodeNormalization": "formC"
              },
              "rules": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "   ",
                  "enabled": true,
                  "order": 0,
                  "exclude": null,
                  "action": {"type":"moveToDirectory","destinationDirectory":"C:\\Target"}
                }
              ]
            }
            """;

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.MissingRequiredField);
        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidRule);
    }

    [Fact]
    public void RelativeDestinationIsRejectedAsInvalidPath()
    {
        string json = MinimalJson(
            NameConditionJson(),
            """{"type":"moveToDirectory","destinationDirectory":"relative\\folder"}""");

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        ConfigurationProblem problem = Assert.Single(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidPath);
        Assert.Equal("/rules/0/action", problem.Location);
    }

    [Fact]
    public void InvalidLiteralRenameIsAnActionProblemRatherThanDestinationPathProblem()
    {
        string json = MinimalJson(
            NameConditionJson(),
            """{"type":"renameToLiteralFileName","fileName":"folder\\name.txt"}""");

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidAction);
        Assert.DoesNotContain(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidPath);
    }

    [Fact]
    public void DuplicateRuleIdsAreRejected()
    {
        string oneRule = MinimalJson(NameConditionJson(), MoveActionJson());
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(oneRule);
        string rule = document.RootElement.GetProperty("rules")[0].GetRawText();
        string json = $$"""
            {
              "schemaVersion": 1,
              "pathPolicy": {
                "syntax": "windows",
                "caseSensitivity": "insensitive",
                "unicodeNormalization": "formC"
              },
              "rules": [{{rule}}, {{rule}}]
            }
            """;

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.DuplicateRuleId);
    }

    [Fact]
    public void UnknownAndDuplicatePropertiesAreNotSilentlyIgnored()
    {
        string json = """
            {
              "schemaVersion": 1,
              "schemaVersion": 1,
              "pathPolicy": {
                "syntax": "windows",
                "caseSensitivity": "insensitive",
                "unicodeNormalization": "formC"
              },
              "rules": [],
              "rulse": []
            }
            """;

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.DuplicateProperty);
        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.UnknownProperty);
    }

    [Fact]
    public void MalformedUtf16IsRejectedBeforeJsonWriting()
    {
        OrganizationConfiguration configuration = new(
            new PathSemantics(
                PathSyntax.Windows,
                PathCaseSensitivity.Insensitive,
                PathUnicodeNormalization.None),
            [
                new ConfiguredOrganizationRule(
                    FirstId,
                    new OrganizationRule(
                        new Rule("bad\ud800name", new GlobCondition("*")),
                        new MoveToDirectoryAction(@"C:\Target"))),
            ]);

        ConfigurationEncodeResult result = OrganizationConfigurationJsonCodec.Encode(configuration);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Problems,
            static problem => problem.Kind == ConfigurationProblemKind.InvalidValue);
    }

    [Fact]
    public void EscapedUnpairedSurrogateIsRejectedDuringDecoding()
    {
        string json = MinimalJson(NameConditionJson(), MoveActionJson()).Replace(
            "\"name\": \"Rule\"",
            "\"name\": \"bad\\uD800name\"",
            StringComparison.Ordinal);

        ConfigurationDecodeResult result = OrganizationConfigurationJsonCodec.Decode(json);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Problems,
            static problem => problem.Kind is ConfigurationProblemKind.InvalidValue
                or ConfigurationProblemKind.MalformedJson);
        Assert.Null(result.Configuration);
    }

    private static OrganizationConfiguration FullConfiguration()
    {
        ICondition complex = new AndCondition(
            new ExtensionCondition(["jpg", ".png"], CaseSensitivity.Sensitive),
            new NameCondition(
                NameTarget.WithoutExtension,
                TextOperator.StartsWith,
                "tatil-",
                CaseSensitivity.Insensitive),
            new GlobCondition("*.tar.gz", NameTarget.FullName, CaseSensitivity.Sensitive),
            new RegexCondition(
                "^IMG_[0-9]+\\.jpg$",
                NameTarget.FullName,
                CaseSensitivity.Insensitive,
                TimeSpan.FromMilliseconds(250)),
            new SizeCondition(ComparisonOperator.GreaterThanOrEqualTo, 4096),
            new TimestampCondition(
                TimestampField.Modified,
                ComparisonOperator.LessThan,
                DateTimeOffset.Parse(
                    "2026-01-02T03:04:05+00:00",
                    CultureInfo.InvariantCulture)),
            new AgeCondition(
                TimestampField.Created,
                ComparisonOperator.GreaterThan,
                TimeSpan.FromDays(30)),
            new OrCondition(
                new NameCondition(NameTarget.FullName, TextOperator.Contains, "özel"),
                new NotCondition(new GlobCondition("*.tmp"))));

        return new OrganizationConfiguration(
            WindowsSemantics(),
            [
                new ConfiguredOrganizationRule(
                    FirstId,
                    new OrganizationRule(
                        new Rule("Görseller", complex, new GlobCondition("*.bak"), false, 20),
                        new CopyToDirectoryAction(@"C:\Arşiv\Görseller"))),
                new ConfiguredOrganizationRule(
                    ThirdId,
                    new OrganizationRule(
                        new Rule("Yeniden adlandır", new GlobCondition("draft-*"), order: 30),
                        new RenameToLiteralFileNameAction("sonuç.txt"))),
                new ConfiguredOrganizationRule(
                    SecondId,
                    new OrganizationRule(
                        new Rule("Büyükler", new SizeCondition(ComparisonOperator.GreaterThan, 10), order: 10),
                        new MoveToDirectoryAction(@"C:\Arşiv\Büyük"))),
            ]);
    }

    private static ConfiguredOrganizationRule Rule(Guid id, int order) =>
        new(
            id,
            new OrganizationRule(
                new Rule("Rule", new GlobCondition("*"), order: order),
                new MoveToDirectoryAction(@"C:\Target")));

    private static PathSemantics WindowsSemantics() =>
        new(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.FormC);

    private static string MinimalJson(string conditionJson, string actionJson) => $$"""
        {
          "schemaVersion": 1,
          "pathPolicy": {
            "syntax": "windows",
            "caseSensitivity": "insensitive",
            "unicodeNormalization": "formC"
          },
          "rules": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Rule",
              "enabled": true,
              "order": 0,
              "include": {{conditionJson}},
              "exclude": null,
              "action": {{actionJson}}
            }
          ]
        }
        """;

    private static string NameConditionJson() =>
        """{"type":"name","target":"fullName","operator":"contains","value":"x","caseSensitivity":"insensitive"}""";

    private static string MoveActionJson() =>
        """{"type":"moveToDirectory","destinationDirectory":"C:\\Target"}""";
}

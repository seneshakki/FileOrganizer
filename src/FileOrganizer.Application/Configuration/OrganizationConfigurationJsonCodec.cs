using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Matching.Logical;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Application.Configuration;

/// <summary>Deliberately maps the stable version-one JSON contract to and from domain objects.</summary>
public static class OrganizationConfigurationJsonCodec
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ConfigurationDecodeResult Decode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid(
                ConfigurationProblemKind.MalformedJson,
                "/",
                "The configuration document is empty.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
            return new Parser(document.RootElement).Parse();
        }
        catch (JsonException)
        {
            return Invalid(
                ConfigurationProblemKind.MalformedJson,
                "/",
                "The configuration document is not valid JSON.");
        }
    }

    public static ConfigurationEncodeResult Encode(OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        List<ConfigurationProblem> problems = ValidateForEncoding(configuration);
        if (problems.Count != 0)
        {
            return new ConfigurationEncodeResult(null, problems);
        }

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = true,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            WritePathPolicy(writer, configuration.PathSemantics);
            writer.WriteStartArray("rules");
            foreach (ConfiguredOrganizationRule configuredRule in configuration.Rules)
            {
                WriteRule(writer, configuredRule);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new ConfigurationEncodeResult(Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n");
    }

    private static ConfigurationDecodeResult Invalid(
        ConfigurationProblemKind kind,
        string location,
        string description) =>
        ConfigurationDecodeResult.Invalid([new ConfigurationProblem(kind, location, description)]);

    private static List<ConfigurationProblem> ValidateForEncoding(
        OrganizationConfiguration configuration)
    {
        List<ConfigurationProblem> problems = [];
        PathPolicy pathPolicy = new(
            configuration.PathSemantics.Syntax,
            configuration.PathSemantics.CaseSensitivity,
            configuration.PathSemantics.UnicodeNormalization);

        for (int index = 0; index < configuration.Rules.Count; index++)
        {
            ConfiguredOrganizationRule configured = configuration.Rules[index];
            string ruleLocation = $"/rules/{index}";
            ValidateText(configured.Rule.Rule.Name, ruleLocation + "/name", configured.Id, problems);
            foreach (EvaluationProblem problem in configured.Rule.Rule.Validate())
            {
                problems.Add(
                    new ConfigurationProblem(
                        ConfigurationProblemKind.InvalidRule,
                        ruleLocation,
                        problem.Description,
                        configured.Id));
            }

            ValidateSerializableCondition(
                configured.Rule.Rule.Include,
                ruleLocation + "/include",
                configured.Id,
                problems);
            if (configured.Rule.Rule.Exclude is not null)
            {
                ValidateSerializableCondition(
                    configured.Rule.Rule.Exclude,
                    ruleLocation + "/exclude",
                    configured.Id,
                    problems);
            }

            foreach (ActionValidationProblem problem in configured.Rule.Action.Validate(pathPolicy))
            {
                problems.Add(
                    new ConfigurationProblem(
                        ConfigurationProblemKind.InvalidAction,
                        ruleLocation + "/action",
                        problem.Description,
                        configured.Id,
                        ActionToken(configured.Rule.Action)));
            }

            switch (configured.Rule.Action)
            {
                case MoveToDirectoryAction move:
                    ValidateText(
                        move.DestinationDirectory,
                        ruleLocation + "/action/destinationDirectory",
                        configured.Id,
                        problems);
                    break;
                case CopyToDirectoryAction copy:
                    ValidateText(
                        copy.DestinationDirectory,
                        ruleLocation + "/action/destinationDirectory",
                        configured.Id,
                        problems);
                    break;
                case RenameToLiteralFileNameAction rename:
                    ValidateText(
                        rename.FileName,
                        ruleLocation + "/action/fileName",
                        configured.Id,
                        problems);
                    break;
            }
        }

        return problems;
    }

    private static void ValidateSerializableCondition(
        ICondition condition,
        string location,
        Guid ruleId,
        List<ConfigurationProblem> problems)
    {
        switch (condition)
        {
            case AndCondition and:
                for (int index = 0; index < and.Children.Count; index++)
                {
                    ValidateSerializableCondition(
                        and.Children[index],
                        $"{location}/conditions/{index}",
                        ruleId,
                        problems);
                }

                break;

            case OrCondition or:
                for (int index = 0; index < or.Children.Count; index++)
                {
                    ValidateSerializableCondition(
                        or.Children[index],
                        $"{location}/conditions/{index}",
                        ruleId,
                        problems);
                }

                break;

            case NotCondition not:
                ValidateSerializableCondition(not.Inner, location + "/condition", ruleId, problems);
                break;

            case ExtensionCondition extension:
                for (int index = 0; index < extension.Extensions.Count; index++)
                {
                    ValidateText(
                        extension.Extensions[index],
                        $"{location}/extensions/{index}",
                        ruleId,
                        problems);
                }

                break;

            case NameCondition name:
                ValidateText(name.Value, location + "/value", ruleId, problems);
                break;

            case GlobCondition glob:
                ValidateText(glob.Pattern, location + "/pattern", ruleId, problems);
                break;

            case RegexCondition regex:
                ValidateText(regex.Pattern, location + "/pattern", ruleId, problems);
                break;

            case SizeCondition:
            case TimestampCondition:
            case AgeCondition:
                break;

            default:
                problems.Add(
                    new ConfigurationProblem(
                        ConfigurationProblemKind.UnsupportedDomainCondition,
                        location,
                        "This condition implementation is not part of configuration schema version 1.",
                        ruleId,
                        condition.GetType().FullName));
                break;
        }
    }

    private static void ValidateText(
        string value,
        string location,
        Guid ruleId,
        List<ConfigurationProblem> problems)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            problems.Add(
                new ConfigurationProblem(
                    ConfigurationProblemKind.InvalidValue,
                    location,
                    "Configuration text contains malformed UTF-16 and cannot be encoded safely.",
                    ruleId));
        }
    }

    private static void WritePathPolicy(Utf8JsonWriter writer, PathSemantics semantics)
    {
        writer.WriteStartObject("pathPolicy");
        writer.WriteString("syntax", PathSyntaxToken(semantics.Syntax));
        writer.WriteString("caseSensitivity", PathCaseToken(semantics.CaseSensitivity));
        writer.WriteString(
            "unicodeNormalization",
            UnicodeNormalizationToken(semantics.UnicodeNormalization));
        writer.WriteEndObject();
    }

    private static void WriteRule(Utf8JsonWriter writer, ConfiguredOrganizationRule configured)
    {
        Rule rule = configured.Rule.Rule;
        writer.WriteStartObject();
        writer.WriteString("id", configured.Id.ToString("D"));
        writer.WriteString("name", rule.Name);
        writer.WriteBoolean("enabled", rule.IsEnabled);
        writer.WriteNumber("order", rule.Order);
        writer.WritePropertyName("include");
        WriteCondition(writer, rule.Include);
        writer.WritePropertyName("exclude");
        if (rule.Exclude is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteCondition(writer, rule.Exclude);
        }

        writer.WritePropertyName("action");
        WriteAction(writer, configured.Rule.Action);
        writer.WriteEndObject();
    }

    private static void WriteCondition(Utf8JsonWriter writer, ICondition condition)
    {
        writer.WriteStartObject();
        switch (condition)
        {
            case ExtensionCondition extension:
                writer.WriteString("type", "extension");
                writer.WriteStartArray("extensions");
                foreach (string value in extension.Extensions)
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
                writer.WriteString("caseSensitivity", CaseToken(extension.CaseSensitivity));
                break;

            case NameCondition name:
                writer.WriteString("type", "name");
                writer.WriteString("target", NameTargetToken(name.Target));
                writer.WriteString("operator", TextOperatorToken(name.Operator));
                writer.WriteString("value", name.Value);
                writer.WriteString("caseSensitivity", CaseToken(name.CaseSensitivity));
                break;

            case GlobCondition glob:
                writer.WriteString("type", "glob");
                writer.WriteString("target", NameTargetToken(glob.Target));
                writer.WriteString("pattern", glob.Pattern);
                writer.WriteString("caseSensitivity", CaseToken(glob.CaseSensitivity));
                break;

            case RegexCondition regex:
                writer.WriteString("type", "regex");
                writer.WriteString("target", NameTargetToken(regex.Target));
                writer.WriteString("pattern", regex.Pattern);
                writer.WriteString("caseSensitivity", CaseToken(regex.CaseSensitivity));
                writer.WriteString("timeout", regex.Timeout.ToString("c", CultureInfo.InvariantCulture));
                break;

            case SizeCondition size:
                writer.WriteString("type", "size");
                writer.WriteString("operator", ComparisonToken(size.Operator));
                writer.WriteNumber("bytes", size.SizeInBytes);
                break;

            case TimestampCondition timestamp:
                writer.WriteString("type", "timestamp");
                writer.WriteString("field", TimestampFieldToken(timestamp.Field));
                writer.WriteString("operator", ComparisonToken(timestamp.Operator));
                writer.WriteString(
                    "thresholdUtc",
                    timestamp.ThresholdUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                break;

            case AgeCondition age:
                writer.WriteString("type", "age");
                writer.WriteString("field", TimestampFieldToken(age.Field));
                writer.WriteString("operator", ComparisonToken(age.Operator));
                writer.WriteString("age", age.Age.ToString("c", CultureInfo.InvariantCulture));
                break;

            case AndCondition and:
                writer.WriteString("type", "and");
                WriteChildren(writer, and.Children);
                break;

            case OrCondition or:
                writer.WriteString("type", "or");
                WriteChildren(writer, or.Children);
                break;

            case NotCondition not:
                writer.WriteString("type", "not");
                writer.WritePropertyName("condition");
                WriteCondition(writer, not.Inner);
                break;

            default:
                throw new InvalidOperationException("Unsupported conditions are rejected before writing.");
        }

        writer.WriteEndObject();
    }

    private static void WriteChildren(Utf8JsonWriter writer, IReadOnlyList<ICondition> children)
    {
        writer.WriteStartArray("conditions");
        foreach (ICondition child in children)
        {
            WriteCondition(writer, child);
        }

        writer.WriteEndArray();
    }

    private static void WriteAction(Utf8JsonWriter writer, OrganizationAction action)
    {
        writer.WriteStartObject();
        writer.WriteString("type", ActionToken(action));
        switch (action)
        {
            case MoveToDirectoryAction move:
                writer.WriteString("destinationDirectory", move.DestinationDirectory);
                break;
            case CopyToDirectoryAction copy:
                writer.WriteString("destinationDirectory", copy.DestinationDirectory);
                break;
            case RenameToLiteralFileNameAction rename:
                writer.WriteString("fileName", rename.FileName);
                break;
        }

        writer.WriteEndObject();
    }

    private static string PathSyntaxToken(PathSyntax value) => value switch
    {
        PathSyntax.Windows => "windows",
        PathSyntax.Posix => "posix",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string PathCaseToken(PathCaseSensitivity value) => value switch
    {
        PathCaseSensitivity.Sensitive => "sensitive",
        PathCaseSensitivity.Insensitive => "insensitive",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string UnicodeNormalizationToken(PathUnicodeNormalization value) => value switch
    {
        PathUnicodeNormalization.None => "none",
        PathUnicodeNormalization.FormC => "formC",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string CaseToken(CaseSensitivity value) => value switch
    {
        CaseSensitivity.Insensitive => "insensitive",
        CaseSensitivity.Sensitive => "sensitive",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string NameTargetToken(NameTarget value) => value switch
    {
        NameTarget.FullName => "fullName",
        NameTarget.WithoutExtension => "withoutExtension",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string TextOperatorToken(TextOperator value) => value switch
    {
        TextOperator.Equals => "equals",
        TextOperator.Contains => "contains",
        TextOperator.StartsWith => "startsWith",
        TextOperator.EndsWith => "endsWith",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ComparisonToken(ComparisonOperator value) => value switch
    {
        ComparisonOperator.LessThan => "lessThan",
        ComparisonOperator.LessThanOrEqualTo => "lessThanOrEqualTo",
        ComparisonOperator.EqualTo => "equalTo",
        ComparisonOperator.GreaterThanOrEqualTo => "greaterThanOrEqualTo",
        ComparisonOperator.GreaterThan => "greaterThan",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string TimestampFieldToken(TimestampField value) => value switch
    {
        TimestampField.Modified => "modified",
        TimestampField.Created => "created",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ActionToken(OrganizationAction action) => action switch
    {
        MoveToDirectoryAction => "moveToDirectory",
        CopyToDirectoryAction => "copyToDirectory",
        RenameToLiteralFileNameAction => "renameToLiteralFileName",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private sealed class Parser(JsonElement root)
    {
        private readonly List<ConfigurationProblem> _problems = [];

        internal ConfigurationDecodeResult Parse()
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(
                    ConfigurationProblemKind.InvalidValue,
                    "/",
                    "The top-level configuration value must be an object.");
            }

            ValidateProperties(root, "/", ["schemaVersion", "pathPolicy", "rules"]);
            int? schemaVersion = ReadInt32(root, "schemaVersion", "/schemaVersion", required: true);
            if (schemaVersion is not null && schemaVersion != CurrentSchemaVersion)
            {
                _problems.Add(
                    new ConfigurationProblem(
                        ConfigurationProblemKind.UnsupportedSchemaVersion,
                        "/schemaVersion",
                        $"Schema version {schemaVersion.Value} is not supported."));
            }

            if (_problems.Any(
                    static problem => problem.Kind == ConfigurationProblemKind.UnsupportedSchemaVersion))
            {
                return ConfigurationDecodeResult.Invalid(_problems);
            }

            PathSemantics? semantics = ParsePathPolicy();
            List<ConfiguredOrganizationRule>? rules = semantics is null
                ? ParseRules(null)
                : ParseRules(
                    new PathPolicy(
                        semantics.Value.Syntax,
                        semantics.Value.CaseSensitivity,
                        semantics.Value.UnicodeNormalization));

            if (_problems.Count != 0 || semantics is null || rules is null)
            {
                return ConfigurationDecodeResult.Invalid(_problems);
            }

            return ConfigurationDecodeResult.Success(
                new OrganizationConfiguration(semantics.Value, rules));
        }

        private PathSemantics? ParsePathPolicy()
        {
            if (!TryGetRequired(root, "pathPolicy", "/pathPolicy", out JsonElement element))
            {
                return null;
            }

            if (!RequireObject(element, "/pathPolicy"))
            {
                return null;
            }

            ValidateProperties(
                element,
                "/pathPolicy",
                ["syntax", "caseSensitivity", "unicodeNormalization"]);
            string? syntaxToken = ReadString(element, "syntax", "/pathPolicy/syntax", required: true);
            string? caseToken = ReadString(
                element,
                "caseSensitivity",
                "/pathPolicy/caseSensitivity",
                required: true);
            string? unicodeToken = ReadString(
                element,
                "unicodeNormalization",
                "/pathPolicy/unicodeNormalization",
                required: true);

            PathSyntax? syntax = ParseToken(
                syntaxToken,
                "/pathPolicy/syntax",
                new Dictionary<string, PathSyntax>(StringComparer.Ordinal)
                {
                    ["windows"] = PathSyntax.Windows,
                    ["posix"] = PathSyntax.Posix,
                });
            PathCaseSensitivity? caseSensitivity = ParseToken(
                caseToken,
                "/pathPolicy/caseSensitivity",
                new Dictionary<string, PathCaseSensitivity>(StringComparer.Ordinal)
                {
                    ["sensitive"] = PathCaseSensitivity.Sensitive,
                    ["insensitive"] = PathCaseSensitivity.Insensitive,
                });
            PathUnicodeNormalization? unicode = ParseToken(
                unicodeToken,
                "/pathPolicy/unicodeNormalization",
                new Dictionary<string, PathUnicodeNormalization>(StringComparer.Ordinal)
                {
                    ["none"] = PathUnicodeNormalization.None,
                    ["formC"] = PathUnicodeNormalization.FormC,
                });

            return syntax is not null && caseSensitivity is not null && unicode is not null
                ? new PathSemantics(syntax.Value, caseSensitivity.Value, unicode.Value)
                : null;
        }

        private List<ConfiguredOrganizationRule>? ParseRules(PathPolicy? pathPolicy)
        {
            if (!TryGetRequired(root, "rules", "/rules", out JsonElement rulesElement))
            {
                return null;
            }

            if (rulesElement.ValueKind != JsonValueKind.Array)
            {
                AddInvalidValue("/rules", "Rules must be an array.");
                return null;
            }

            List<ConfiguredOrganizationRule> rules = [];
            HashSet<Guid> ids = [];
            int index = 0;
            foreach (JsonElement element in rulesElement.EnumerateArray())
            {
                ConfiguredOrganizationRule? rule = ParseRule(element, index, pathPolicy);
                if (rule is not null)
                {
                    if (!ids.Add(rule.Id))
                    {
                        _problems.Add(
                            new ConfigurationProblem(
                                ConfigurationProblemKind.DuplicateRuleId,
                                $"/rules/{index}/id",
                                "Rule IDs must be unique.",
                                rule.Id));
                    }
                    else
                    {
                        rules.Add(rule);
                    }
                }

                index++;
            }

            return rules;
        }

        private ConfiguredOrganizationRule? ParseRule(
            JsonElement element,
            int index,
            PathPolicy? pathPolicy)
        {
            string location = $"/rules/{index}";
            if (!RequireObject(element, location))
            {
                return null;
            }

            ValidateProperties(
                element,
                location,
                ["id", "name", "enabled", "order", "include", "exclude", "action"]);
            string? idText = ReadString(element, "id", location + "/id", required: true);
            Guid? id = null;
            if (idText is not null)
            {
                if (!Guid.TryParseExact(idText, "D", out Guid parsedId) || parsedId == Guid.Empty)
                {
                    AddInvalidValue(location + "/id", "A rule ID must be a non-empty GUID in D format.");
                }
                else
                {
                    id = parsedId;
                }
            }

            string? name = ReadString(element, "name", location + "/name", required: true);
            bool? enabled = ReadBoolean(element, "enabled", location + "/enabled", required: true);
            int? order = ReadInt32(element, "order", location + "/order", required: true);

            ICondition? include = TryGetRequired(
                element,
                "include",
                location + "/include",
                out JsonElement includeElement)
                    ? ParseCondition(includeElement, location + "/include", id)
                    : null;

            ICondition? exclude = null;
            if (element.TryGetProperty("exclude", out JsonElement excludeElement)
                && excludeElement.ValueKind != JsonValueKind.Null)
            {
                exclude = ParseCondition(excludeElement, location + "/exclude", id);
            }

            OrganizationAction? action = TryGetRequired(
                element,
                "action",
                location + "/action",
                out JsonElement actionElement)
                    ? ParseAction(actionElement, location + "/action", id, pathPolicy)
                    : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (name is not null)
                {
                    _problems.Add(
                        new ConfigurationProblem(
                            ConfigurationProblemKind.InvalidRule,
                            location + "/name",
                            "A rule name cannot be empty or whitespace.",
                            id));
                }

                return null;
            }

            if (id is null || enabled is null || order is null || include is null || action is null)
            {
                return null;
            }

            Rule matchingRule = new(name, include, exclude, enabled.Value, order.Value);
            return new ConfiguredOrganizationRule(id.Value, new OrganizationRule(matchingRule, action));
        }

        private ICondition? ParseCondition(JsonElement element, string location, Guid? ruleId)
        {
            if (!RequireObject(element, location))
            {
                return null;
            }

            string? type = ReadString(element, "type", location + "/type", required: true);
            if (type is null)
            {
                return null;
            }

            return type switch
            {
                "extension" => ParseExtension(element, location, ruleId, type),
                "name" => ParseName(element, location, ruleId, type),
                "glob" => ParseGlob(element, location, ruleId, type),
                "regex" => ParseRegex(element, location, ruleId, type),
                "size" => ParseSize(element, location, ruleId, type),
                "timestamp" => ParseTimestamp(element, location, ruleId, type),
                "age" => ParseAge(element, location, ruleId, type),
                "and" => ParseComposite(element, location, ruleId, type, isAnd: true),
                "or" => ParseComposite(element, location, ruleId, type, isAnd: false),
                "not" => ParseNot(element, location, ruleId, type),
                _ => UnknownCondition(type, location, ruleId),
            };
        }

        private ICondition? ParseExtension(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(element, location, ["type", "extensions", "caseSensitivity"]);
            List<string>? extensions = ReadStringArray(
                element,
                "extensions",
                location + "/extensions",
                required: true);
            CaseSensitivity? caseSensitivity = ParseCaseSensitivity(element, location);
            return extensions is not null && caseSensitivity is not null
                ? ValidateCondition(
                    new ExtensionCondition(extensions, caseSensitivity.Value),
                    location,
                    ruleId,
                    type)
                : null;
        }

        private ICondition? ParseName(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(
                element,
                location,
                ["type", "target", "operator", "value", "caseSensitivity"]);
            NameTarget? target = ParseNameTarget(element, location);
            TextOperator? textOperator = ParseTextOperator(element, location);
            string? value = ReadString(element, "value", location + "/value", required: true);
            CaseSensitivity? caseSensitivity = ParseCaseSensitivity(element, location);
            return target is not null
                && textOperator is not null
                && value is not null
                && caseSensitivity is not null
                    ? ValidateCondition(
                        new NameCondition(
                            target.Value,
                            textOperator.Value,
                            value,
                            caseSensitivity.Value),
                        location,
                        ruleId,
                        type)
                    : null;
        }

        private ICondition? ParseGlob(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(
                element,
                location,
                ["type", "target", "pattern", "caseSensitivity"]);
            NameTarget? target = ParseNameTarget(element, location);
            string? pattern = ReadString(element, "pattern", location + "/pattern", required: true);
            CaseSensitivity? caseSensitivity = ParseCaseSensitivity(element, location);
            return target is not null && pattern is not null && caseSensitivity is not null
                ? ValidateCondition(
                    new GlobCondition(pattern, target.Value, caseSensitivity.Value),
                    location,
                    ruleId,
                    type)
                : null;
        }

        private ICondition? ParseRegex(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(
                element,
                location,
                ["type", "target", "pattern", "caseSensitivity", "timeout"]);
            NameTarget? target = ParseNameTarget(element, location);
            string? pattern = ReadString(element, "pattern", location + "/pattern", required: true);
            CaseSensitivity? caseSensitivity = ParseCaseSensitivity(element, location);
            TimeSpan? timeout = ParseTimeSpan(element, "timeout", location + "/timeout");
            return target is not null
                && pattern is not null
                && caseSensitivity is not null
                && timeout is not null
                    ? ValidateCondition(
                        new RegexCondition(
                            pattern,
                            target.Value,
                            caseSensitivity.Value,
                            timeout.Value),
                        location,
                        ruleId,
                        type)
                    : null;
        }

        private ICondition? ParseSize(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(element, location, ["type", "operator", "bytes"]);
            ComparisonOperator? comparison = ParseComparison(element, location);
            long? bytes = ReadInt64(element, "bytes", location + "/bytes", required: true);
            return comparison is not null && bytes is not null
                ? ValidateCondition(
                    new SizeCondition(comparison.Value, bytes.Value),
                    location,
                    ruleId,
                    type)
                : null;
        }

        private ICondition? ParseTimestamp(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(element, location, ["type", "field", "operator", "thresholdUtc"]);
            TimestampField? field = ParseTimestampField(element, location);
            ComparisonOperator? comparison = ParseComparison(element, location);
            string? thresholdText = ReadString(
                element,
                "thresholdUtc",
                location + "/thresholdUtc",
                required: true);
            DateTimeOffset? threshold = null;
            if (thresholdText is not null)
            {
                if (!DateTimeOffset.TryParseExact(
                        thresholdText,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTimeOffset parsed)
                    || parsed.Offset != TimeSpan.Zero)
                {
                    AddInvalidValue(
                        location + "/thresholdUtc",
                        "A timestamp threshold must be an ISO round-trip timestamp in UTC.");
                }
                else
                {
                    threshold = parsed;
                }
            }

            return field is not null && comparison is not null && threshold is not null
                ? ValidateCondition(
                    new TimestampCondition(field.Value, comparison.Value, threshold.Value),
                    location,
                    ruleId,
                    type)
                : null;
        }

        private ICondition? ParseAge(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(element, location, ["type", "field", "operator", "age"]);
            TimestampField? field = ParseTimestampField(element, location);
            ComparisonOperator? comparison = ParseComparison(element, location);
            TimeSpan? age = ParseTimeSpan(element, "age", location + "/age");
            return field is not null && comparison is not null && age is not null
                ? ValidateCondition(
                    new AgeCondition(field.Value, comparison.Value, age.Value),
                    location,
                    ruleId,
                    type)
                : null;
        }

        private ICondition? ParseComposite(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type,
            bool isAnd)
        {
            ValidateProperties(element, location, ["type", "conditions"]);
            if (!TryGetRequired(
                    element,
                    "conditions",
                    location + "/conditions",
                    out JsonElement childrenElement))
            {
                return null;
            }

            if (childrenElement.ValueKind != JsonValueKind.Array)
            {
                AddInvalidValue(location + "/conditions", "Logical conditions must contain an array.");
                return null;
            }

            if (childrenElement.GetArrayLength() == 0)
            {
                _problems.Add(
                    new ConfigurationProblem(
                        ConfigurationProblemKind.ImpossibleLogicalTree,
                        location + "/conditions",
                        "AND and OR conditions require at least one child.",
                        ruleId,
                        type));
                return null;
            }

            List<ICondition> children = [];
            int index = 0;
            foreach (JsonElement childElement in childrenElement.EnumerateArray())
            {
                ICondition? child = ParseCondition(
                    childElement,
                    $"{location}/conditions/{index}",
                    ruleId);
                if (child is not null)
                {
                    children.Add(child);
                }

                index++;
            }

            if (children.Count != childrenElement.GetArrayLength())
            {
                return null;
            }

            return isAnd ? new AndCondition([.. children]) : new OrCondition([.. children]);
        }

        private NotCondition? ParseNot(
            JsonElement element,
            string location,
            Guid? ruleId,
            string type)
        {
            ValidateProperties(element, location, ["type", "condition"]);
            if (!TryGetRequired(
                    element,
                    "condition",
                    location + "/condition",
                    out JsonElement inner))
            {
                return null;
            }

            if (inner.ValueKind == JsonValueKind.Null)
            {
                _problems.Add(
                    new ConfigurationProblem(
                        ConfigurationProblemKind.ImpossibleLogicalTree,
                        location + "/condition",
                        "A NOT condition requires one child condition.",
                        ruleId,
                        type));
                return null;
            }

            ICondition? condition = ParseCondition(inner, location + "/condition", ruleId);
            return condition is null ? null : new NotCondition(condition);
        }

        private ICondition? ValidateCondition(
            ICondition condition,
            string location,
            Guid? ruleId,
            string discriminator)
        {
            EvaluationProblem[] problems = [.. condition.Validate()];
            foreach (EvaluationProblem problem in problems)
            {
                _problems.Add(
                    new ConfigurationProblem(
                        ConfigurationProblemKind.InvalidCondition,
                        location,
                        problem.Description,
                        ruleId,
                        discriminator));
            }

            return condition;
        }

        private ICondition? UnknownCondition(string type, string location, Guid? ruleId)
        {
            _problems.Add(
                new ConfigurationProblem(
                    ConfigurationProblemKind.UnknownConditionDiscriminator,
                    location + "/type",
                    $"Condition type '{type}' is not supported by schema version 1.",
                    ruleId,
                    type));
            return null;
        }

        private OrganizationAction? ParseAction(
            JsonElement element,
            string location,
            Guid? ruleId,
            PathPolicy? pathPolicy)
        {
            if (!RequireObject(element, location))
            {
                return null;
            }

            string? type = ReadString(element, "type", location + "/type", required: true);
            if (type is null)
            {
                return null;
            }

            OrganizationAction? action;
            switch (type)
            {
                case "moveToDirectory":
                case "copyToDirectory":
                    ValidateProperties(element, location, ["type", "destinationDirectory"]);
                    string? destination = ReadString(
                        element,
                        "destinationDirectory",
                        location + "/destinationDirectory",
                        required: true);
                    action = destination is null
                        ? null
                        : type == "moveToDirectory"
                            ? new MoveToDirectoryAction(destination)
                            : new CopyToDirectoryAction(destination);
                    break;

                case "renameToLiteralFileName":
                    ValidateProperties(element, location, ["type", "fileName"]);
                    string? fileName = ReadString(
                        element,
                        "fileName",
                        location + "/fileName",
                        required: true);
                    action = fileName is null ? null : new RenameToLiteralFileNameAction(fileName);
                    break;

                default:
                    _problems.Add(
                        new ConfigurationProblem(
                            ConfigurationProblemKind.UnknownActionDiscriminator,
                            location + "/type",
                            $"Action type '{type}' is not supported by schema version 1.",
                            ruleId,
                            type));
                    return null;
            }

            if (action is null || pathPolicy is null)
            {
                return action;
            }

            foreach (ActionValidationProblem problem in action.Validate(pathPolicy))
            {
                _problems.Add(
                    new ConfigurationProblem(
                        problem.Kind == ActionValidationProblemKind.InvalidDestinationDirectory
                            ? ConfigurationProblemKind.InvalidPath
                            : ConfigurationProblemKind.InvalidAction,
                        location,
                        problem.Description,
                        ruleId,
                        type));
            }

            return action;
        }

        private CaseSensitivity? ParseCaseSensitivity(JsonElement element, string location) =>
            ParseToken(
                ReadString(
                    element,
                    "caseSensitivity",
                    location + "/caseSensitivity",
                    required: true),
                location + "/caseSensitivity",
                new Dictionary<string, CaseSensitivity>(StringComparer.Ordinal)
                {
                    ["insensitive"] = CaseSensitivity.Insensitive,
                    ["sensitive"] = CaseSensitivity.Sensitive,
                });

        private NameTarget? ParseNameTarget(JsonElement element, string location) =>
            ParseToken(
                ReadString(element, "target", location + "/target", required: true),
                location + "/target",
                new Dictionary<string, NameTarget>(StringComparer.Ordinal)
                {
                    ["fullName"] = NameTarget.FullName,
                    ["withoutExtension"] = NameTarget.WithoutExtension,
                });

        private TextOperator? ParseTextOperator(JsonElement element, string location) =>
            ParseToken(
                ReadString(element, "operator", location + "/operator", required: true),
                location + "/operator",
                new Dictionary<string, TextOperator>(StringComparer.Ordinal)
                {
                    ["equals"] = TextOperator.Equals,
                    ["contains"] = TextOperator.Contains,
                    ["startsWith"] = TextOperator.StartsWith,
                    ["endsWith"] = TextOperator.EndsWith,
                });

        private ComparisonOperator? ParseComparison(JsonElement element, string location) =>
            ParseToken(
                ReadString(element, "operator", location + "/operator", required: true),
                location + "/operator",
                new Dictionary<string, ComparisonOperator>(StringComparer.Ordinal)
                {
                    ["lessThan"] = ComparisonOperator.LessThan,
                    ["lessThanOrEqualTo"] = ComparisonOperator.LessThanOrEqualTo,
                    ["equalTo"] = ComparisonOperator.EqualTo,
                    ["greaterThanOrEqualTo"] = ComparisonOperator.GreaterThanOrEqualTo,
                    ["greaterThan"] = ComparisonOperator.GreaterThan,
                });

        private TimestampField? ParseTimestampField(JsonElement element, string location) =>
            ParseToken(
                ReadString(element, "field", location + "/field", required: true),
                location + "/field",
                new Dictionary<string, TimestampField>(StringComparer.Ordinal)
                {
                    ["modified"] = TimestampField.Modified,
                    ["created"] = TimestampField.Created,
                });

        private T? ParseToken<T>(
            string? token,
            string location,
            IReadOnlyDictionary<string, T> values)
            where T : struct
        {
            if (token is null)
            {
                return null;
            }

            if (values.TryGetValue(token, out T value))
            {
                return value;
            }

            AddInvalidValue(location, $"Token '{token}' is not recognized.");
            return null;
        }

        private TimeSpan? ParseTimeSpan(JsonElement element, string name, string location)
        {
            string? text = ReadString(element, name, location, required: true);
            if (text is null)
            {
                return null;
            }

            if (TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out TimeSpan value))
            {
                return value;
            }

            AddInvalidValue(location, "A duration must use the invariant constant format.");
            return null;
        }

        private List<string>? ReadStringArray(
            JsonElement parent,
            string name,
            string location,
            bool required)
        {
            if (!parent.TryGetProperty(name, out JsonElement element))
            {
                if (required)
                {
                    AddMissing(location);
                }

                return null;
            }

            if (element.ValueKind != JsonValueKind.Array)
            {
                AddInvalidValue(location, "This value must be an array of strings.");
                return null;
            }

            List<string> values = [];
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    AddInvalidValue($"{location}/{index}", "This value must be a string.");
                }
                else
                {
                    string? value = ReadJsonStringValue(item, $"{location}/{index}");
                    if (value is not null && IsWellFormedText(value, $"{location}/{index}"))
                    {
                        values.Add(value);
                    }
                }

                index++;
            }

            return values.Count == element.GetArrayLength() ? values : null;
        }

        private string? ReadString(
            JsonElement parent,
            string name,
            string location,
            bool required)
        {
            if (!parent.TryGetProperty(name, out JsonElement element))
            {
                if (required)
                {
                    AddMissing(location);
                }

                return null;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                AddInvalidValue(location, "This value must be a string.");
                return null;
            }

            string? value = ReadJsonStringValue(element, location);
            return value is not null && IsWellFormedText(value, location) ? value : null;
        }

        private string? ReadJsonStringValue(JsonElement element, string location)
        {
            try
            {
                return element.GetString();
            }
            catch (InvalidOperationException)
            {
                AddInvalidValue(
                    location,
                    "Configuration text contains an incomplete Unicode surrogate pair.");
                return null;
            }
        }

        private bool IsWellFormedText(string value, string location)
        {
            try
            {
                _ = StrictUtf8.GetByteCount(value);
                return true;
            }
            catch (EncoderFallbackException)
            {
                AddInvalidValue(
                    location,
                    "Configuration text contains malformed UTF-16 and cannot be decoded safely.");
                return false;
            }
        }

        private bool? ReadBoolean(
            JsonElement parent,
            string name,
            string location,
            bool required)
        {
            if (!parent.TryGetProperty(name, out JsonElement element))
            {
                if (required)
                {
                    AddMissing(location);
                }

                return null;
            }

            if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                AddInvalidValue(location, "This value must be a Boolean.");
                return null;
            }

            return element.GetBoolean();
        }

        private int? ReadInt32(
            JsonElement parent,
            string name,
            string location,
            bool required)
        {
            if (!parent.TryGetProperty(name, out JsonElement element))
            {
                if (required)
                {
                    AddMissing(location);
                }

                return null;
            }

            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            {
                AddInvalidValue(location, "This value must be a 32-bit integer.");
                return null;
            }

            return value;
        }

        private long? ReadInt64(
            JsonElement parent,
            string name,
            string location,
            bool required)
        {
            if (!parent.TryGetProperty(name, out JsonElement element))
            {
                if (required)
                {
                    AddMissing(location);
                }

                return null;
            }

            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long value))
            {
                AddInvalidValue(location, "This value must be a 64-bit integer.");
                return null;
            }

            return value;
        }

        private bool TryGetRequired(
            JsonElement parent,
            string name,
            string location,
            out JsonElement element)
        {
            if (parent.TryGetProperty(name, out element))
            {
                return true;
            }

            AddMissing(location);
            return false;
        }

        private bool RequireObject(JsonElement element, string location)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            AddInvalidValue(location, "This value must be an object.");
            return false;
        }

        private void ValidateProperties(
            JsonElement element,
            string location,
            params string[] allowed)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string propertyLocation = location == "/"
                    ? "/" + property.Name
                    : location + "/" + property.Name;
                if (!seen.Add(property.Name))
                {
                    _problems.Add(
                        new ConfigurationProblem(
                            ConfigurationProblemKind.DuplicateProperty,
                            propertyLocation,
                            $"Property '{property.Name}' appears more than once."));
                }

                if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                {
                    _problems.Add(
                        new ConfigurationProblem(
                            ConfigurationProblemKind.UnknownProperty,
                            propertyLocation,
                            $"Property '{property.Name}' is not part of schema version 1."));
                }
            }
        }

        private void AddMissing(string location) =>
            _problems.Add(
                new ConfigurationProblem(
                    ConfigurationProblemKind.MissingRequiredField,
                    location,
                    "A required configuration field is missing."));

        private void AddInvalidValue(string location, string description) =>
            _problems.Add(
                new ConfigurationProblem(
                    ConfigurationProblemKind.InvalidValue,
                    location,
                    description));
    }
}

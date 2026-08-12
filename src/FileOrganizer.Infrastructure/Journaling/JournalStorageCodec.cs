using System.Text.Json;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>
/// Encodes the durable human-readable representation. Integrity never depends
/// on JSON property order, whitespace, or line-ending spelling.
/// </summary>
internal static class JournalStorageCodec
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    internal static byte[] EncodeGenesis(StoredJournalGenesis genesis)
    {
        ArgumentNullException.ThrowIfNull(genesis);

        using MemoryStream output = new();
        using (Utf8JsonWriter json = NewWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("journalFormatVersion", genesis.JournalFormatVersion);
            json.WriteNumber("canonicalHashVersion", genesis.CanonicalHashVersion);
            json.WriteString("runId", genesis.RunId);
            json.WritePropertyName("manifest");
            WriteManifest(json, genesis.Manifest);
            json.WritePropertyName("startRecord");
            WriteRecord(json, genesis.StartRecord);
            json.WriteString("manifestHash", genesis.ManifestHash.Value);
            json.WriteEndObject();
        }

        return output.ToArray();
    }

    internal static byte[] EncodeGenesis(
        FileOrganizer.Application.Journaling.ExecutionRunId runId,
        FileOrganizer.Application.Execution.ExecutionManifest manifest,
        FileOrganizer.Application.Journaling.ExecutionJournalRecord startRecord) =>
        EncodeGenesis(JournalCanonicalHash.CreateGenesis(runId, manifest, startRecord));

    internal static StoredJournalGenesis DecodeGenesis(ReadOnlySpan<byte> utf8Json)
    {
        using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
        JsonElement root = document.RootElement;
        RequireObject(
            root,
            "journal genesis",
            "journalFormatVersion",
            "canonicalHashVersion",
            "runId",
            "manifest",
            "startRecord",
            "manifestHash");

        return new StoredJournalGenesis(
            RequiredInt32(root, "journalFormatVersion"),
            RequiredInt32(root, "canonicalHashVersion"),
            RequiredString(root, "runId"),
            ReadManifest(Required(root, "manifest")),
            ReadRecord(Required(root, "startRecord")),
            new JournalHash(RequiredString(root, "manifestHash")));
    }

    internal static byte[] EncodeRecord(StoredJournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using MemoryStream output = new();
        using (Utf8JsonWriter json = NewWriter(output))
        {
            WriteRecord(json, record);
        }

        return output.ToArray();
    }

    internal static byte[] EncodeRecord(
        FileOrganizer.Application.Journaling.ExecutionJournalRecord record,
        JournalHash previousRecordHash) =>
        EncodeRecord(JournalCanonicalHash.CreateRecord(record, previousRecordHash));

    internal static StoredJournalRecord DecodeRecord(ReadOnlySpan<byte> utf8Json)
    {
        using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
        return ReadRecord(document.RootElement);
    }

    private static Utf8JsonWriter NewWriter(Stream output) => new(
        output,
        new JsonWriterOptions
        {
            Indented = true,
            SkipValidation = false,
        });

    private static void WriteManifest(Utf8JsonWriter json, StoredExecutionManifest manifest)
    {
        json.WriteStartObject();
        json.WriteString("fingerprint", manifest.Fingerprint);
        json.WriteNumber("fingerprintFormatVersion", manifest.FingerprintFormatVersion);
        json.WriteNumber("generatedAtUtcTicks", manifest.GeneratedAtUtcTicks);
        json.WritePropertyName("pathSemantics");
        json.WriteStartObject();
        json.WriteString("syntax", JournalWireTokens.ToToken(manifest.PathSemantics.Syntax));
        json.WriteString(
            "caseSensitivity",
            JournalWireTokens.ToToken(manifest.PathSemantics.CaseSensitivity));
        json.WriteString(
            "unicodeNormalization",
            JournalWireTokens.ToToken(manifest.PathSemantics.UnicodeNormalization));
        json.WriteEndObject();
        json.WriteString("sourceRootPath", manifest.SourceRootPath);
        json.WritePropertyName("sourceRootIdentity");
        WriteIdentity(json, manifest.SourceRootIdentity);
        json.WritePropertyName("items");
        json.WriteStartArray();
        foreach (StoredManifestItem item in manifest.Items)
        {
            WriteManifestItem(json, item);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static StoredExecutionManifest ReadManifest(JsonElement element)
    {
        RequireObject(
            element,
            "stored manifest",
            "fingerprint",
            "fingerprintFormatVersion",
            "generatedAtUtcTicks",
            "pathSemantics",
            "sourceRootPath",
            "sourceRootIdentity",
            "items");

        JsonElement semanticsElement = Required(element, "pathSemantics");
        RequireObject(
            semanticsElement,
            "path semantics",
            "syntax",
            "caseSensitivity",
            "unicodeNormalization");
        PathSemantics semantics = new(
            JournalWireTokens.ParsePathSyntax(RequiredString(semanticsElement, "syntax")),
            JournalWireTokens.ParseCaseSensitivity(
                RequiredString(semanticsElement, "caseSensitivity")),
            JournalWireTokens.ParseUnicodeNormalization(
                RequiredString(semanticsElement, "unicodeNormalization")));

        JsonElement itemsElement = Required(element, "items");
        RequireKind(itemsElement, JsonValueKind.Array, "items");
        List<StoredManifestItem> items = [];
        foreach (JsonElement item in itemsElement.EnumerateArray())
        {
            items.Add(ReadManifestItem(item));
        }

        return new StoredExecutionManifest(
            RequiredString(element, "fingerprint"),
            RequiredInt32(element, "fingerprintFormatVersion"),
            RequiredInt64(element, "generatedAtUtcTicks"),
            semantics,
            RequiredString(element, "sourceRootPath"),
            ReadIdentity(Required(element, "sourceRootIdentity")),
            items);
    }

    private static void WriteManifestItem(Utf8JsonWriter json, StoredManifestItem item)
    {
        json.WriteStartObject();
        json.WriteNumber("ordinal", item.Ordinal);
        json.WriteString("actionKind", JournalWireTokens.ToToken(item.ActionKind));
        json.WriteString("sourcePath", item.SourcePath);
        json.WritePropertyName("sourceIdentity");
        WriteIdentity(json, item.SourceIdentity);
        json.WriteNumber("sourceLength", item.SourceLength);
        WriteOptionalInt64(json, "sourceCreatedUtcTicks", item.SourceCreatedUtcTicks);
        json.WriteNumber("sourceModifiedUtcTicks", item.SourceModifiedUtcTicks);
        json.WriteString("destinationDirectoryPath", item.DestinationDirectoryPath);
        json.WritePropertyName("destinationDirectoryIdentity");
        WriteIdentity(json, item.DestinationDirectoryIdentity);
        json.WriteString("targetPath", item.TargetPath);
        json.WriteString("selectedRuleName", item.SelectedRuleName);
        json.WriteNumber("selectedRuleOrder", item.SelectedRuleOrder);
        json.WriteEndObject();
    }

    private static StoredManifestItem ReadManifestItem(JsonElement element)
    {
        RequireObject(
            element,
            "stored manifest item",
            "ordinal",
            "actionKind",
            "sourcePath",
            "sourceIdentity",
            "sourceLength",
            "sourceCreatedUtcTicks",
            "sourceModifiedUtcTicks",
            "destinationDirectoryPath",
            "destinationDirectoryIdentity",
            "targetPath",
            "selectedRuleName",
            "selectedRuleOrder");

        return new StoredManifestItem(
            RequiredInt32(element, "ordinal"),
            JournalWireTokens.ParseActionKind(RequiredString(element, "actionKind")),
            RequiredString(element, "sourcePath"),
            ReadIdentity(Required(element, "sourceIdentity")),
            RequiredInt64(element, "sourceLength"),
            OptionalInt64(element, "sourceCreatedUtcTicks"),
            RequiredInt64(element, "sourceModifiedUtcTicks"),
            RequiredString(element, "destinationDirectoryPath"),
            ReadIdentity(Required(element, "destinationDirectoryIdentity")),
            RequiredString(element, "targetPath"),
            RequiredString(element, "selectedRuleName"),
            RequiredInt32(element, "selectedRuleOrder"));
    }

    private static void WriteRecord(Utf8JsonWriter json, StoredJournalRecord record)
    {
        json.WriteStartObject();
        json.WriteNumber("journalFormatVersion", record.JournalFormatVersion);
        json.WriteNumber("canonicalHashVersion", record.CanonicalHashVersion);
        json.WritePropertyName("payload");
        WriteRecordPayload(json, record.Payload);
        json.WriteString("payloadHash", record.PayloadHash.Value);
        json.WriteEndObject();
    }

    private static StoredJournalRecord ReadRecord(JsonElement element)
    {
        RequireObject(
            element,
            "stored journal record",
            "journalFormatVersion",
            "canonicalHashVersion",
            "payload",
            "payloadHash");

        return new StoredJournalRecord(
            RequiredInt32(element, "journalFormatVersion"),
            RequiredInt32(element, "canonicalHashVersion"),
            ReadRecordPayload(Required(element, "payload")),
            new JournalHash(RequiredString(element, "payloadHash")));
    }

    private static void WriteRecordPayload(
        Utf8JsonWriter json,
        StoredJournalRecordPayload payload)
    {
        json.WriteStartObject();
        json.WriteString("fingerprint", payload.Fingerprint);
        json.WriteString("runId", payload.RunId);
        json.WriteNumber("sequence", payload.Sequence);
        WriteOptionalHash(json, "previousRecordHash", payload.PreviousRecordHash);
        json.WriteNumber("recordedAtUtcTicks", payload.RecordedAtUtcTicks);
        json.WriteString("kind", JournalWireTokens.ToToken(payload.Kind));
        WriteOptionalInt32(json, "operationOrdinal", payload.OperationOrdinal);
        json.WritePropertyName("expectedEntryIdentity");
        if (payload.ExpectedEntryIdentity is null)
        {
            json.WriteNullValue();
        }
        else
        {
            WriteIdentity(json, payload.ExpectedEntryIdentity);
        }

        json.WriteString("reason", JournalWireTokens.ToToken(payload.Reason));
        json.WriteEndObject();
    }

    private static StoredJournalRecordPayload ReadRecordPayload(JsonElement element)
    {
        RequireObject(
            element,
            "journal record payload",
            "fingerprint",
            "runId",
            "sequence",
            "previousRecordHash",
            "recordedAtUtcTicks",
            "kind",
            "operationOrdinal",
            "expectedEntryIdentity",
            "reason");

        JsonElement identityElement = Required(element, "expectedEntryIdentity");
        StoredPhysicalIdentity? identity = identityElement.ValueKind == JsonValueKind.Null
            ? null
            : ReadIdentity(identityElement);

        return new StoredJournalRecordPayload(
            RequiredString(element, "fingerprint"),
            RequiredString(element, "runId"),
            RequiredInt64(element, "sequence"),
            OptionalHash(element, "previousRecordHash"),
            RequiredInt64(element, "recordedAtUtcTicks"),
            JournalWireTokens.ParseRecordKind(RequiredString(element, "kind")),
            OptionalInt32(element, "operationOrdinal"),
            identity,
            JournalWireTokens.ParseReason(RequiredString(element, "reason")));
    }

    private static void WriteIdentity(Utf8JsonWriter json, StoredPhysicalIdentity identity)
    {
        json.WriteStartObject();
        json.WriteString("scheme", identity.Scheme);
        json.WriteNumber("schemeVersion", identity.SchemeVersion);
        json.WriteString("volumeIdentity", identity.VolumeIdentity);
        json.WriteString("entryIdentifier", identity.EntryIdentifier);
        json.WriteString("entryKind", JournalWireTokens.ToToken(identity.EntryKind));
        json.WriteEndObject();
    }

    private static StoredPhysicalIdentity ReadIdentity(JsonElement element)
    {
        RequireObject(
            element,
            "physical identity",
            "scheme",
            "schemeVersion",
            "volumeIdentity",
            "entryIdentifier",
            "entryKind");

        return new StoredPhysicalIdentity(
            RequiredString(element, "scheme"),
            RequiredInt32(element, "schemeVersion"),
            RequiredString(element, "volumeIdentity"),
            RequiredString(element, "entryIdentifier"),
            JournalWireTokens.ParsePhysicalKind(RequiredString(element, "entryKind")));
    }

    private static void WriteOptionalHash(
        Utf8JsonWriter json,
        string name,
        JournalHash? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value.Value);
        }
    }

    private static JournalHash? OptionalHash(JsonElement parent, string name)
    {
        JsonElement element = Required(parent, name);
        return element.ValueKind == JsonValueKind.Null
            ? null
            : new JournalHash(RequiredString(parent, name));
    }

    private static void WriteOptionalInt64(
        Utf8JsonWriter json,
        string name,
        long? value)
    {
        if (value.HasValue)
        {
            json.WriteNumber(name, value.Value);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteOptionalInt32(
        Utf8JsonWriter json,
        string name,
        int? value)
    {
        if (value.HasValue)
        {
            json.WriteNumber(name, value.Value);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static long? OptionalInt64(JsonElement parent, string name)
    {
        JsonElement element = Required(parent, name);
        return element.ValueKind == JsonValueKind.Null
            ? null
            : RequiredInt64(parent, name);
    }

    private static int? OptionalInt32(JsonElement parent, string name)
    {
        JsonElement element = Required(parent, name);
        return element.ValueKind == JsonValueKind.Null
            ? null
            : RequiredInt32(parent, name);
    }

    private static JsonElement Required(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            throw new JsonException($"Required property '{name}' is missing.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        JsonElement element = Required(parent, name);
        RequireKind(element, JsonValueKind.String, name);
        return element.GetString()!;
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        JsonElement element = Required(parent, name);
        RequireKind(element, JsonValueKind.Number, name);
        if (!element.TryGetInt32(out int value))
        {
            throw new JsonException($"Property '{name}' is not an Int32.");
        }

        return value;
    }

    private static long RequiredInt64(JsonElement parent, string name)
    {
        JsonElement element = Required(parent, name);
        RequireKind(element, JsonValueKind.Number, name);
        if (!element.TryGetInt64(out long value))
        {
            throw new JsonException($"Property '{name}' is not an Int64.");
        }

        return value;
    }

    private static void RequireObject(
        JsonElement element,
        string description,
        params string[] expectedProperties)
    {
        RequireKind(element, JsonValueKind.Object, description);
        HashSet<string> expected = new(expectedProperties, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException(
                    $"The {description} contains duplicate property '{property.Name}'.");
            }

            if (!expected.Contains(property.Name))
            {
                throw new JsonException(
                    $"The {description} contains unsupported property '{property.Name}'.");
            }
        }

        foreach (string property in expected)
        {
            if (!seen.Contains(property))
            {
                throw new JsonException(
                    $"The {description} is missing required property '{property}'.");
            }
        }
    }

    private static void RequireKind(
        JsonElement element,
        JsonValueKind expected,
        string description)
    {
        if (element.ValueKind != expected)
        {
            throw new JsonException(
                $"The {description} must be a JSON {expected}, not {element.ValueKind}.");
        }
    }
}

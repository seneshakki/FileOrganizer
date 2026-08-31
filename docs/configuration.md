# Configuration reference

FileOrganizer configuration is human-readable UTF-8 JSON with an explicit top-level schema version. The current and only supported version is `1`.

Configuration describes planning behavior and watched roots. It does not contain scan results, physical identities, plans, journals, leases, or recovery state. Loading configuration activates only the watched-root and read-only preview runtime; this desktop screen cannot execute file operations.

## Start and edit a configuration

Start the desktop application with no arguments for a first-run workspace. No configuration file is read or created until an explicit action is taken. Alternatively, load one absolute file path at startup:

```powershell
FileOrganizer.App.exe --config "C:\FileOrganizer\config.json"
```

The only accepted startup forms are no arguments or exactly `--config <absolute file path>`. A relative path, filesystem root, or any additional argument is rejected before configuration-file I/O.

The Configuration tab operations are:

- **Open** selects and reads an existing file. A valid document becomes the active read-only preview configuration. Invalid JSON is shown unchanged for editing and does not replace the previous active preview.
- **New / Create** selects a destination and publishes an empty Windows schema-v1 document, then activates it. If `--config` selected a missing path, Create uses that path. Creation never happens implicitly, and the final publication is create-only: if another process creates the file first, FileOrganizer reports a conflict and replaces nothing.
- **Reload** rereads the current path. It is disabled while there are unsaved edits.
- **Validate** checks the in-memory draft only. It neither writes the file nor changes the active preview.
- **Save** first validates the draft, then requires the on-disk document to have the exact byte revision captured by the last load or successful publication. A mismatch is reported as `ChangedExternally`; the external file and active preview are preserved. A matching draft is published as normalized pretty JSON, and only then is the saved configuration activated. Invalid drafts and publication failures perform no activation. A runtime activation failure can occur after a successful save and is reported separately without discarding a previously active preview runtime.
- **Revert edits** restores the exact text and validation state from the last successful load or publication. It does not write a file.
- **Import** validates a complete source document before conditionally replacing the current configuration file. Its destination must still match the loaded revision, or remain absent when a new destination was selected. Import is replacement, not merge, and is disabled while there are unsaved edits.
- **Export** validates the current draft and publishes a normalized independent file. The export destination must differ from the current document and active configuration paths both lexically and, for existing Windows files, by strong physical identity. An alias or unprovable identity fails closed. Export does not change the current path, active preview, or dirty state.

Open, Create, Reload, and Import are disabled while the draft is dirty so an operation cannot silently discard unsaved text. The editor is read-only while an operation is in progress.

Closing the main window with a dirty draft requires an explicit **Save**, **Discard**, or
**Cancel** choice. Save closes only after the draft is actually published; invalid drafts,
storage failures, and external-change conflicts keep the workspace open. Cancel is the
fail-safe default if the prompt closes without a selection.

## Top-level document

```json
{
  "schemaVersion": 1,
  "pathPolicy": {
    "syntax": "windows",
    "caseSensitivity": "insensitive",
    "unicodeNormalization": "formC"
  },
  "watchRoots": [],
  "rules": []
}
```

| Property | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | Yes | A 32-bit integer. It must be `1`. |
| `pathPolicy` | Yes | The lexical path rules used by watch roots and actions. |
| `watchRoots` | No when reading old v1; always written | Ordered watched-root definitions. A missing property loads as an empty list. |
| `rules` | Yes | Organization rules. Runtime evaluation order is derived from each rule's `order`. |

Property names and tokens are case-sensitive. Unknown properties, duplicate properties, JSON comments, trailing commas, and documents nested beyond the parser's depth limit are rejected rather than ignored.

## Path policy

`pathPolicy` has exactly three fields:

| Field | Supported tokens |
| --- | --- |
| `syntax` | `windows`, `posix` |
| `caseSensitivity` | `sensitive`, `insensitive` |
| `unicodeNormalization` | `none`, `formC` |

The Windows desktop should normally use:

```json
{
  "syntax": "windows",
  "caseSensitivity": "insensitive",
  "unicodeNormalization": "formC"
}
```

Windows paths must be fully qualified drive-rooted paths such as `C:\Inbox` or complete UNC paths. Device/extended-length forms, traversal above a root, NUL, reserved characters and device names, and components ending in a space or dot are invalid. POSIX paths must start at `/`; the exactly-two-leading-slashes form is unsupported.

Watch-root paths have an additional rule: the JSON spelling must already be the canonical lexical spelling produced by the selected policy. For example, with Windows syntax use `C:\Inbox`, not `C:/Inbox` or `C:\Folder\..\Inbox`. Paths that are logically equivalent under the selected case and Unicode policy count as duplicates.

Configuration validation is lexical and does not require a watched directory to exist. Missing, inaccessible, or otherwise unsupported physical roots are reported later as watcher/runtime diagnostics.

## Watch roots

Each `watchRoots` item has this shape:

```json
{
  "id": "10000000-0000-0000-0000-000000000001",
  "rootPath": "C:\\Inbox",
  "enabled": true,
  "includeSubdirectories": false
}
```

- `id` is a caller-supplied, non-empty GUID in hyphenated `D` format. It is persistence identity; paths and display text are not identities.
- `rootPath` is an absolute, canonically spelled path under `pathPolicy`.
- `enabled` controls whether this root participates in monitoring.
- `includeSubdirectories` controls recursive observation.

Watch-root IDs must be unique. Root paths must also be logically unique under `pathPolicy`.

Starting or resuming a watcher does not by itself establish trusted state. The runtime performs a read-only baseline scan before treating incremental events as synchronized. Overflow and lost-event uncertainty require resynchronization. Watcher activity produces stable candidates and historical plans only; it never executes an action.

## Rules

Every rule uses this shape:

```json
{
  "id": "20000000-0000-0000-0000-000000000001",
  "name": "Readable rule name",
  "enabled": true,
  "order": 10,
  "include": { "type": "glob", "target": "fullName", "pattern": "*.pdf", "caseSensitivity": "insensitive" },
  "exclude": null,
  "action": { "type": "moveToDirectory", "destinationDirectory": "C:\\Organized\\PDF" }
}
```

- `id` is a unique, non-empty GUID in `D` format.
- `name` must contain non-whitespace text.
- `enabled` controls evaluation, but disabled rules are still validated.
- `order` is a 32-bit integer. Lower values run first; original document order breaks equal-order ties. Saving emits rules in this evaluation order.
- `include` is required and must contain one condition.
- `exclude` may be omitted or `null`; otherwise it contains one condition that vetoes the rule. Exclusion is evaluated first for safety.
- `action` is required and describes a planning intent.

### Common condition tokens

| Setting | Supported tokens |
| --- | --- |
| `caseSensitivity` | `sensitive`, `insensitive` |
| `target` | `fullName`, `withoutExtension` |
| text `operator` | `equals`, `contains`, `startsWith`, `endsWith` |
| comparison `operator` | `lessThan`, `lessThanOrEqualTo`, `equalTo`, `greaterThanOrEqualTo`, `greaterThan` |
| timestamp `field` | `modified`, `created` |

`fullName` includes the extension; `withoutExtension` does not. Text matching is ordinal. A creation timestamp can be unavailable on a physical filesystem; such a condition becomes indeterminate rather than inventing a value.

### Condition discriminators

All condition objects require a recognized `type` discriminator and only the fields shown below.

#### `extension`

```json
{ "type": "extension", "extensions": [".pdf", ".txt", ""], "caseSensitivity": "insensitive" }
```

`extensions` must be a non-empty string array. A leading dot is optional and is normalized when saved. An exact empty string means files without an extension. Whitespace-only entries, `.` by itself, and multi-dot extensions such as `.tar.gz` are invalid; use a glob for compound suffixes.

#### `name`

```json
{ "type": "name", "target": "withoutExtension", "operator": "startsWith", "value": "invoice-", "caseSensitivity": "insensitive" }
```

`value` cannot be empty. The supported `target`, text `operator`, and `caseSensitivity` tokens are listed above.

#### `glob`

```json
{ "type": "glob", "target": "fullName", "pattern": "invoice-*.pdf", "caseSensitivity": "insensitive" }
```

The pattern cannot be blank. `*` matches any run of characters and `?` matches exactly one character. All other characters are literal; there are no character classes, escapes, path matching, or special `**` behavior.

#### `regex`

```json
{ "type": "regex", "target": "fullName", "pattern": "^IMG_[0-9]+\\.jpg$", "caseSensitivity": "insensitive", "timeout": "00:00:00.1000000" }
```

The pattern must be non-blank and valid. `timeout` is a positive duration in invariant .NET constant (`c`) format. Regex matching is culture-invariant and time-bounded.

#### `size`

```json
{ "type": "size", "operator": "greaterThanOrEqualTo", "bytes": 1048576 }
```

`bytes` is a non-negative 64-bit integer. Use `and` with two size conditions to express a range.

#### `timestamp`

```json
{ "type": "timestamp", "field": "modified", "operator": "lessThan", "thresholdUtc": "2026-01-02T03:04:05.0000000+00:00" }
```

`thresholdUtc` must use the ISO round-trip (`O`) representation with a zero UTC offset. This is a fixed instant; use `age` for a moving threshold.

#### `age`

```json
{ "type": "age", "field": "modified", "operator": "greaterThan", "age": "30.00:00:00" }
```

`age` is a non-negative duration in invariant .NET constant (`c`) format. A file older than the threshold uses `greaterThan` because its calculated age is larger.

#### `and` and `or`

```json
{
  "type": "and",
  "conditions": [
    { "type": "glob", "target": "fullName", "pattern": "*.pdf", "caseSensitivity": "insensitive" },
    { "type": "size", "operator": "greaterThan", "bytes": 0 }
  ]
}
```

Both discriminators use a non-empty `conditions` array. Children may be any supported condition, including another logical condition.

#### `not`

```json
{
  "type": "not",
  "condition": { "type": "glob", "target": "fullName", "pattern": "~*", "caseSensitivity": "insensitive" }
}
```

`not` requires exactly one non-null child in `condition`. Negating an indeterminate observation remains indeterminate.

## Actions

Schema v1 recognizes three action discriminators:

| `type` | Required value | Current desktop behavior |
| --- | --- | --- |
| `moveToDirectory` | `destinationDirectory`: absolute path valid under `pathPolicy` | Planning and read-only preview only in this UI |
| `copyToDirectory` | `destinationDirectory`: absolute path valid under `pathPolicy` | Planning-only; no physical Copy implementation is enabled |
| `renameToLiteralFileName` | `fileName`: one valid literal filename component | Planning-only; no physical literal Rename implementation is enabled |

Examples:

```json
{ "type": "moveToDirectory", "destinationDirectory": "C:\\Organized\\PDF" }
```

```json
{ "type": "copyToDirectory", "destinationDirectory": "C:\\Backup\\PDF" }
```

```json
{ "type": "renameToLiteralFileName", "fileName": "archived.pdf" }
```

A literal rename filename cannot be empty, `.` or `..`, contain a slash/backslash/NUL, or violate the selected syntax's filename restrictions. Merely serializing an action never authorizes or performs it.

## Complete valid example

This example uses nested `and`, `or`, and `not` conditions and one watched root. It is valid schema-v1 JSON for the Windows policy shown.

```json
{
  "schemaVersion": 1,
  "pathPolicy": {
    "syntax": "windows",
    "caseSensitivity": "insensitive",
    "unicodeNormalization": "formC"
  },
  "watchRoots": [
    {
      "id": "10000000-0000-0000-0000-000000000001",
      "rootPath": "C:\\Inbox",
      "enabled": true,
      "includeSubdirectories": true
    }
  ],
  "rules": [
    {
      "id": "20000000-0000-0000-0000-000000000001",
      "name": "Filed PDF documents",
      "enabled": true,
      "order": 10,
      "include": {
        "type": "and",
        "conditions": [
          {
            "type": "extension",
            "extensions": [".pdf"],
            "caseSensitivity": "insensitive"
          },
          {
            "type": "or",
            "conditions": [
              {
                "type": "name",
                "target": "withoutExtension",
                "operator": "startsWith",
                "value": "invoice-",
                "caseSensitivity": "insensitive"
              },
              {
                "type": "regex",
                "target": "fullName",
                "pattern": "^receipt-[0-9]{4}-[0-9]{2}\\.pdf$",
                "caseSensitivity": "insensitive",
                "timeout": "00:00:00.1000000"
              }
            ]
          },
          {
            "type": "size",
            "operator": "greaterThanOrEqualTo",
            "bytes": 1024
          },
          {
            "type": "not",
            "condition": {
              "type": "glob",
              "target": "fullName",
              "pattern": "~*",
              "caseSensitivity": "insensitive"
            }
          }
        ]
      },
      "exclude": {
        "type": "age",
        "field": "modified",
        "operator": "lessThan",
        "age": "00:10:00"
      },
      "action": {
        "type": "moveToDirectory",
        "destinationDirectory": "C:\\Organized\\PDF"
      }
    }
  ]
}
```

## Validation and publication guarantees

Malformed JSON never becomes runtime configuration. Validation reports stable problem categories with JSON-pointer-like locations, including unsupported versions, missing fields, unknown or duplicate properties, invalid tokens and values, duplicate IDs or logical roots, invalid paths, impossible logical trees, invalid conditions, and invalid actions.

Configuration reads require valid UTF-8; a UTF-8 BOM is accepted. Saves write pretty UTF-8 without a BOM and use an explicit property layout, culture-invariant numbers/durations/timestamps, deterministic rule order, and normalized domain values. This is stable human-readable output, not a byte-canonical hashing format.

Saving writes and flushes a complete temporary file in the destination directory before publication. Existing Windows configurations are atomically replaced with `File.Replace`; new files use a no-overwrite rename. Create uses an atomic create-if-absent publication. Save and Import compare an exact-byte SHA-256 revision repeatedly, including immediately before replacement, so edits observed before the final validation are reported instead of overwritten. Export compares existing protected paths using Windows `FILE_ID_INFO`, covering hard-link and extended-path aliases; an unavailable identity fails closed. Unsupported atomic replacement or any publication failure is reported rather than weakened to a copy/delete fallback. Configuration files and existing path ancestors may not be reparse points. Missing destination directories may be created for the selected configuration path.

On Windows, every participating FileOrganizer writer in the current login session acquires one fixed named publication mutex before validation, staging, flush, and publication. The fixed name means drive, extended-path, UNC, 8.3, and hard-link spellings cannot select separate writer locks. Acquisition is bounded to 30 seconds; timeout or synchronization failure returns `PublicationSynchronizationUnavailable`, while cancellation during acquisition returns `PublicationSynchronizationCanceled`. If a prior owner ended unexpectedly, the mutex is reacquired and every write condition is evaluated again before publication.

A failure before publication preserves the previous valid file. Documented managed APIs do
not provide an atomic compare-and-replace-by-revision operation, so a writer outside the named
mutex protocol, including one in another Windows session, can still race the final validation
and replacement window whether that writer is benign or malicious. The implementation also
does not claim that a newly published directory entry survives every sudden-power-loss event
because .NET exposes no portable directory-flush operation.

## Version compatibility

Version `1` is the only supported schema. An older v1 document that omits `watchRoots` loads with an empty watch-root list; newly saved documents always emit `watchRoots`. A future or otherwise unsupported `schemaVersion` fails clearly and is not migrated or activated.

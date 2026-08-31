# FileOrganizer

[![CI](https://github.com/seneshakki/FileOrganizer/actions/workflows/ci.yml/badge.svg)](https://github.com/seneshakki/FileOrganizer/actions/workflows/ci.yml)
[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

FileOrganizer is a safety-focused, Windows-first desktop application for building and inspecting deterministic file-organization plans. The current desktop surface provides a versioned JSON configuration workspace, watched-root monitoring, and read-only planning previews.

The desktop UI does **not** expose organization execution. Configuration commands may create missing parent directories for, and publish, the explicitly selected configuration document using a same-directory staging artifact; they do not execute organization actions against watched candidates. The explicitly selected configuration document remains writable even if its path is inside a watched root. `CopyToDirectory` and literal `Rename` actions can be represented in configuration for planning, but remain planning-only.

## Public beta

FileOrganizer is currently in beta. Download the newest Windows x64 portable
build from [GitHub Releases](https://github.com/seneshakki/FileOrganizer/releases).
Each release provides a self-contained archive and a separate SHA-256 checksum
file.

The beta has no installer, automatic updater, or Authenticode signature. Windows
SmartScreen or publisher warnings may therefore appear. Extract the complete ZIP
to a new directory before starting `FileOrganizer.App.exe`.

## Requirements

- Windows x64 on a Windows release supported by
  [.NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md#win)
- .NET SDK selected by [`global.json`](global.json) when building from source

## Run from source

```powershell
dotnet restore FileOrganizer.slnx `
  --locked-mode `
  --configfile scripts/NuGet.Release.Config
dotnet build FileOrganizer.slnx --no-restore
dotnet test FileOrganizer.slnx --no-build
dotnet run --no-restore --project src/FileOrganizer.App/FileOrganizer.App.csproj
```

Starting with no arguments opens an empty configuration workspace and performs no configuration-file I/O. Open or create a configuration explicitly in the **Configuration** tab.

To load a known configuration at startup, pass its absolute path:

```powershell
dotnet run --no-restore --project src/FileOrganizer.App/FileOrganizer.App.csproj -- --config "C:\FileOrganizer\config.json"
```

`--config <absolute file path>` is the only supported startup argument. A missing file is not created automatically; the UI reports it and enables an explicit **New / Create** workflow.

See [Configuration reference](docs/configuration.md) for the complete schema-v1 contract, supported conditions and actions, validation rules, and editor behavior.

See [Safety model](docs/safety-model.md) for the watcher, preview, journal,
recovery, and native-move trust boundaries.

## Configuration workspace

The desktop editor supports:

- opening an existing UTF-8 JSON document;
- creating an empty schema-v1 document only while the selected path remains absent;
- validating a draft without writing or activating it;
- conditionally publishing a validated draft only while the loaded file revision still matches,
  with same-session FileOrganizer writers serialized before activating its read-only preview
  runtime;
- reverting unsaved editor changes;
- importing a complete validated document without implicit merging; and
- exporting a validated independent copy without changing the active configuration.

Invalid source text remains editable and is never activated. Closing a dirty workspace
requires an explicit Save, Discard, or Cancel decision. The dashboard shows watcher state
and the latest immutable planning preview only; it has no execution control.

## Windows portable package

Create and verify a self-contained directory package at a new absent direct-child path under `artifacts\portable`:

```powershell
.\scripts\Publish-WindowsPortable.ps1 `
  -OutputDirectory "$PWD\artifacts\portable\win-x64-release"
```

Run the application with no arguments from the resulting directory:

```powershell
.\FileOrganizer.App.exe
```

The package is directory-based: trimming and single-file extraction are intentionally disabled for the current Avalonia desktop build. Every release archive includes the MIT license and the notices for its shipped third-party components.

See [Windows portable release](docs/release.md) for the complete build gate,
package verifier, evidence boundaries, and acceptance checklist.

## Contributing and security

Contributions are welcome under the [MIT License](LICENSE). Read
[CONTRIBUTING.md](CONTRIBUTING.md) before changing behavior or safety boundaries.
Report suspected vulnerabilities privately according to [SECURITY.md](SECURITY.md),
not through a public issue.

## Architecture

- `FileOrganizer.App` — Avalonia desktop composition and presentation
- `FileOrganizer.Application` — configuration mapping, orchestration, and state machines
- `FileOrganizer.Core` — portable immutable domain and planning models
- `FileOrganizer.Infrastructure` — filesystem, watcher, journal, and Windows adapters

Dependencies point toward the domain: UI composes Application, Application uses Core, and Infrastructure implements physical ports. Core contains no JSON or filesystem I/O.

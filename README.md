# FileOrganizer

FileOrganizer is a safety-focused, Windows-first desktop application for building and inspecting deterministic file-organization plans. The current desktop surface provides a versioned JSON configuration workspace, watched-root monitoring, and read-only planning previews.

The desktop UI does **not** expose organization execution. Configuration commands may create missing parent directories for, and publish, the explicitly selected configuration document using a same-directory staging artifact; they do not move, copy, rename, or delete watched candidate/source files. `CopyToDirectory` and literal `Rename` actions can be represented in configuration for planning, but remain planning-only.

## Requirements

- Windows x64 for the supported desktop package
- .NET SDK selected by [`global.json`](global.json) when building from source

## Run from source

```powershell
dotnet restore FileOrganizer.slnx
dotnet build FileOrganizer.slnx --no-restore
dotnet test FileOrganizer.slnx --no-build
dotnet run --project src/FileOrganizer.App/FileOrganizer.App.csproj
```

Starting with no arguments opens an empty configuration workspace and performs no configuration-file I/O. Open or create a configuration explicitly in the **Configuration** tab.

To load a known configuration at startup, pass its absolute path:

```powershell
dotnet run --project src/FileOrganizer.App/FileOrganizer.App.csproj -- --config "C:\FileOrganizer\config.json"
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

The package is directory-based: trimming and single-file extraction are intentionally disabled for the current Avalonia desktop build.

See [Windows portable release](docs/release.md) for the complete build gate,
package verifier, evidence boundaries, and acceptance checklist.

## Architecture

- `FileOrganizer.App` — Avalonia desktop composition and presentation
- `FileOrganizer.Application` — configuration mapping, orchestration, and state machines
- `FileOrganizer.Core` — portable immutable domain and planning models
- `FileOrganizer.Infrastructure` — filesystem, watcher, journal, and Windows adapters

Dependencies point toward the domain: UI composes Application, Application uses Core, and Infrastructure implements physical ports. Core contains no JSON or filesystem I/O.

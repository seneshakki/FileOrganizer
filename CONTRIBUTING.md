# Contributing to FileOrganizer

Thank you for helping improve FileOrganizer. The project prioritizes explicit
safety guarantees over convenience fallbacks.

## Development setup

Use Windows and the .NET SDK selected by `global.json`:

```powershell
dotnet restore FileOrganizer.slnx `
  --locked-mode `
  --configfile scripts/NuGet.Release.Config
dotnet build FileOrganizer.slnx --no-restore
dotnet test FileOrganizer.slnx --no-build
```

Warnings fail the build. Keep package versions in `Directory.Packages.props` and
commit the deliberately regenerated lock files with any dependency change.
Regenerate and recheck both dependency graphs only when changing packages:

```powershell
dotnet restore FileOrganizer.slnx `
  --force-evaluate `
  --configfile scripts/NuGet.Release.Config
dotnet restore src/FileOrganizer.App/FileOrganizer.App.csproj `
  --runtime win-x64 `
  --force-evaluate `
  --configfile scripts/NuGet.Release.Config
dotnet restore FileOrganizer.slnx `
  --locked-mode `
  --configfile scripts/NuGet.Release.Config
dotnet restore src/FileOrganizer.App/FileOrganizer.App.csproj `
  --runtime win-x64 `
  --locked-mode `
  --configfile scripts/NuGet.Release.Config
```

Re-verify `THIRD-PARTY-NOTICES.txt` whenever a package, runtime pack, or release
RID changes.

## Pull requests

1. Open an issue before a large behavioral or architecture change.
2. Keep one focused concern per pull request.
3. Add deterministic tests for changed behavior.
4. Run an incremental solution build and the relevant tests while developing.
5. Run the complete build and test suite before requesting review.
6. Explain the safety impact and any behavior that remains untested.

Use subjects such as `feat(core): ...`, `fix(app): ...`, and `docs: ...`.

## Safety rules

- Core remains platform-independent and filesystem-I/O-free.
- Application owns orchestration and state machines.
- Infrastructure owns physical filesystem and Windows adapters.
- Tests that touch the filesystem must use unique isolated temporary paths.
- Never test against Desktop, Documents, Downloads, or other real user folders.
- Do not add overwrite, copy-delete fallback, automatic rollback, or silent
  safety downgrade behavior.
- A planning preview is historical information, never execution authorization.

Read [AGENTS.md](AGENTS.md) and the [safety model](docs/safety-model.md) before
changing watcher, configuration persistence, journal, recovery, or move code.

## Security reports

Do not disclose vulnerabilities in a public issue. Follow
[SECURITY.md](SECURITY.md).

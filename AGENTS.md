# Repository Guidelines

## Project Structure and Architecture

FileOrganizer is a safety-focused, Windows-first file organization application. Source lives under `src/`; tests under `tests/`.

- `FileOrganizer.App`: Avalonia UI.
- `FileOrganizer.Application`: orchestration and state machines.
- `FileOrganizer.Core`: portable domain models.
- `FileOrganizer.Infrastructure`: physical adapters.

Dependency direction is `App/UI -> Application -> Core` and `Infrastructure -> Application + Core`. Core stays platform-independent and filesystem-I/O-free. Application owns orchestration and state machines; Infrastructure owns OS and physical I/O adapters.

## Build, Test, and Run

Use the SDK selected by `global.json`. Package versions belong in `Directory.Packages.props`.

```powershell
dotnet restore FileOrganizer.slnx
dotnet build FileOrganizer.slnx --no-restore
dotnet test FileOrganizer.slnx --no-build
dotnet run --project src/FileOrganizer.App/FileOrganizer.App.csproj
```

Run focused tests while implementing. Before each commit, run an incremental solution build and relevant tests. Before completing a sub-milestone, run the full build and suite. Avoid clean builds, full restores, or the entire suite in the inner edit loop unless required.

## Style and Testing

Follow `.editorconfig`: four-space C# indentation, two-space project/XML/AXAML indentation, file-scoped namespaces, required braces, `PascalCase` public members, `IInterface`, and `_camelCase` private fields. Warnings fail builds. Prefer built-in .NET and documented platform APIs.

Use xUnit v3, `*Tests` classes, and behavior-named methods. Assert exact outcomes and problem kinds. Core and Application tests stay filesystem-free; Infrastructure tests use isolated temporary directories, never real user or repository folders.

## Safety and Milestone Scope

Never introduce user-file mutation unless the active milestone explicitly authorizes it. No overwrite, replacement, copy-delete fallback, automatic rollback, or silent unsafe fallback. Never substitute a weaker guarantee when a required guarantee cannot be established; stop and report the blocker. Do not broaden the active milestone. If focused investigation leaves feasibility or safety unresolved, report it instead of starting open-ended research.

Do not perform external research unless repository inspection and platform documentation are insufficient. When necessary, prefer primary official documentation.

## Git and Reporting

Do not create a Git remote or push before the publication milestone. Never amend, rebase, squash, force-push, or rewrite existing history. Keep sub-milestones narrow and use 1-3 coherent commits where natural. Follow subjects such as `feat(core): ...` and `chore: ...`.

Pull requests describe safety and behavior impact, link the issue or milestone, include build/test results, and add screenshots only for UI changes. Do not narrate successful commands or repeat the plan while working; show detailed output only for a real failure or decision. Keep reports to decisions, commits, build, tests, Git status, blockers, and deferred risks.

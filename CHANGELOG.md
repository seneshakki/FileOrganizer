# Changelog

All notable user-facing changes are documented here. FileOrganizer follows
[Semantic Versioning](https://semver.org/) while releases are available.

## [Unreleased]

### Fixed

- Release packaging now retries bounded transient Windows directory-lock failures
  while revalidating the generated path and reparse boundary before every attempt.

## [0.1.0-beta.1] - 2026-08-31

### Added

- Versioned JSON configuration workspace with validation, import, and export.
- Watched-root lifecycle, overflow/resynchronization handling, debounce, and
  physical stability observation.
- Immutable read-only organization-plan previews in the Avalonia desktop UI.
- Durable move-only execution, journal, lease, and recovery backend retained
  behind an unbound desktop safety boundary.
- Verified Windows x64 self-contained portable packaging workflow.

### Security and safety

- The desktop UI does not expose physical organization execution.
- Copy, literal rename, delete, overwrite, rollback, and automatic resume are
  not physically executable from the desktop application.
- Portable packages are checked for exact commit identity, unexpected payloads,
  alternate data streams, diagnostics leakage, and forbidden mutation surfaces.

[Unreleased]: https://github.com/seneshakki/FileOrganizer/compare/v0.1.0-beta.1...HEAD
[0.1.0-beta.1]: https://github.com/seneshakki/FileOrganizer/releases/tag/v0.1.0-beta.1

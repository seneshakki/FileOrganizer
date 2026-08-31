# Security policy

## Supported releases

FileOrganizer is currently in public beta. Security fixes are provided for the
latest published beta only.

| Version | Supported |
| --- | --- |
| Latest published beta | Yes |
| Older builds | No |

## Report a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private
[security advisory form](https://github.com/seneshakki/FileOrganizer/security/advisories/new)
instead.

Include the affected version, Windows version, a minimal reproduction, and the
expected safety impact. Redact real file names, user paths, configuration data,
journal contents, and other personal information. Use an isolated temporary
directory when reproducing filesystem behavior.

Reports are reviewed on a best-effort basis. Please wait for a coordinated fix
before publishing exploit details.

## Security boundary

The current desktop UI provides configuration editing, watched-root monitoring,
and read-only planning previews. It does not expose the repository's physical
execution backend. See the [safety model](docs/safety-model.md) for the exact
implemented and unsupported guarantees.

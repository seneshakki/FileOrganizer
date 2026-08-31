# Windows portable release

This document describes the local, directory-based Windows x64 release workflow.
It does not authorize an installer, code signing, uploading, pushing, or any other
external publication.

## Prerequisites

- Run the commands from the repository root on Windows.
- Install a .NET SDK accepted by `global.json`. The requested SDK is .NET SDK
  `10.0.302`; `latestFeature` roll-forward is enabled and prerelease SDKs are not
  accepted.
- Ensure `dotnet`, Git, and PowerShell are available on `PATH`.
- Allow access to `https://api.nuget.org/v3/index.json`. The authoritative
  publish uses an empty isolated package cache, disables the HTTP cache, and
  intentionally does not fall back to machine-configured package sources.
- Reserve enough free space for restore/publish intermediates and the complete
  directory-based package. Package size is not a stable release contract.
- Start from a reviewed working tree. Do not package unrelated local changes.

## Build and test gate

The following commands are useful as a fast local preflight:

```powershell
dotnet restore FileOrganizer.slnx
dotnet build FileOrganizer.slnx --no-restore
dotnet test FileOrganizer.slnx --no-build
```

They are not the authoritative release gate. The publish script repeats restore,
Release build, and the full test suite from an isolated archive of the exact
resolved commit. A package is not accepted when that gate fails. Warnings are
build failures in this repository.

## Publish and verify

The default command publishes to `artifacts\portable\win-x64`. That directory
must be absent:

```powershell
.\scripts\Publish-WindowsPortable.ps1

$packagePath = [IO.Path]::GetFullPath('artifacts\portable\win-x64')
$expectedCommit = (git rev-parse --verify HEAD).Trim().ToLowerInvariant()
.\scripts\Test-WindowsPortablePackage.ps1 `
    -PackageDirectory $packagePath `
    -ExpectedCommit $expectedCommit
```

The publish script creates a fresh bare local clone in a unique workspace beneath
the system temporary directory, verifies the exact commit there, and archives only
that isolated object database. Caller Git repository-selection, replacement,
attribute-source, system/global configuration, and template inputs are neutralized;
the committed tree may not use `export-ignore` or `export-subst`, and the extracted
regular-file set must exactly match the committed tree. The running publish script
must also match its committed copy, apart from line-ending representation.

From that source workspace, with the committed `global.json`, repository build
properties, release-only NuGet source file, and an empty isolated package cache,
the script performs the full Release build/test gate, a `win-x64` restore, a
self-contained publish, a focused mutation-boundary scan of the exact published
`FileOrganizer.App.dll`, and package verification. Persistent build servers are
disabled so isolated package files are not retained by reused MSBuild nodes. The
second command is an explicit, repeatable re-verification step.

For another local candidate, select a new absolute direct child of
`artifacts\portable`:

```powershell
$releaseStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packagePath = [IO.Path]::GetFullPath(
    (Join-Path $PWD "artifacts\portable\win-x64-$releaseStamp"))

.\scripts\Publish-WindowsPortable.ps1 -OutputDirectory $packagePath
$expectedCommit = (git rev-parse --verify HEAD).Trim().ToLowerInvariant()
.\scripts\Test-WindowsPortablePackage.ps1 `
    -PackageDirectory $packagePath `
    -ExpectedCommit $expectedCommit
```

Do not reuse any existing candidate path, even if it is empty. A new absent path
keeps the evidence for one publish isolated from prior output.

## Output safety behavior

`Publish-WindowsPortable.ps1` deliberately constrains its output boundary:

- The resolved output must be a direct child of `artifacts\portable`; a relative
  path, the artifact root itself, a nested descendant, or a path outside that
  root is rejected.
- The tracked and non-ignored untracked working tree must be clean. Initial and
  final HEAD/status checks run with caller repository-selection and replacement
  environment cleared. The script builds only the exact isolated committed tree
  outside the repository and passes that commit to the package verifier.
- The source snapshot comes from a fresh bare `--no-local` clone with an empty
  Git template, global config, and global attributes file. Local info attributes,
  replace refs/grafts, alternate object directories, indexes, and work-tree
  redirection cannot select or rewrite the archived release source. The archive
  must contain every regular file in the commit and no extra file.
- The isolated commands run with the archived source as their working directory,
  require its committed `global.json`, anchor the repository's committed MSBuild
  property files, and disable parent `Directory.Build.targets` discovery.
- Restore uses only the committed release NuGet source declaration, a fresh
  package directory, and no HTTP cache. Machine-configured package sources and
  live-checkout source files are not release inputs.
- After isolated publish and verification, but before acceptance, the script
  requires the same live-checkout commit and a still-clean working tree. A
  candidate produced while either changed remains on disk for inspection but is
  not accepted.
- Any existing output path, including an empty directory, is rejected. The
  script does not overwrite, merge with, or clean a previous package.
- Publish first targets a unique sibling staging directory. The committed
  verifier checks it before a same-volume `Directory.Move` publishes it to the
  still-absent final path without replacement; the committed verifier then
  checks the final directory again. Concurrent cooperating publish runs cannot
  merge their contents.
- A failed prepublication staging directory and the isolated source workspace
  are removed only through exact generated-path checks. Cleanup failures are
  reported without replacing the original build/publish error; an otherwise
  successful candidate with a cleanup warning is not accepted.
- Before publish, every existing output ancestor back to the repository root is
  checked and any reparse point causes a fail-stop.
- Verification rejects a package root that is a reparse point and rejects every
  reparse entry found inside the package.
- Dependency asset paths must be relative and cannot contain empty, `.` or `..`
  segments; path escape attempts fail verification.

These checks protect the normal local packaging workflow. They are not a claim
of race-free publication against another process that can replace path
components or alter staging/final package contents around verification, whether
accidentally or deliberately. Run release preparation in a trusted local
checkout with access restricted to the release operator.

## Package properties

The accepted package is:

- Windows x64 (`win-x64`) and self-contained; a separate .NET runtime install is
  not required on the destination machine.
- Its entry executable has an x64 PE machine header. This structural check does
  not prove that Windows can launch or keep the process running.
- Directory-based. `FileOrganizer.App.exe` must remain alongside all published
  files; this is not a single-file executable.
- Untrimmed and not ReadyToRun, preserving Avalonia XAML and native dependency
  probing behavior.
- Built without release debug symbols. Native and managed `.pdb` files are
  excluded.
- Built with Avalonia diagnostics explicitly disabled in the runtime
  configuration and excluded from the dependency graph.
- Every first-party `FileOrganizer` executable and assembly identifies the exact
  expected 40-character Git commit in `ProductVersion`.
- The exact RID-specific `FileOrganizer.App.dll` produced for the candidate is
  scanned after publish for forbidden execution surfaces, direct filesystem
  mutation bindings, process-launch/native escape hatches, and P/Invoke methods;
  the earlier solution test build is not substituted for this artifact check.
  Both required scans are selected by exact test name and must appear as passed
  in the deterministic TRX result; zero, missing, skipped, or extra matches fail.
- Checked for its app, application, core, infrastructure, critical host/runtime
  files, and every non-symbol `runtime`, `native`, `resources`, or
  `runtimeTargets` asset declared by the selected `win-x64` dependency target;
  every checked file must exist and be non-empty.
- Rejected if source/project files, symbols, common signing or secret-bearing
  files, repository/build directories, or any payload outside the selected
  dependency inventory and the exact apphost/deps/runtime-config files is present.
- Inventory paths are compared ordinally, and named NTFS alternate data streams
  on the package root or any descendant are rejected rather than treated as
  invisible package content.

The verifier reports the resolved directory, expected commit, runtime target,
file count, total bytes, self-contained status, and diagnostics status. File
count and byte size are observations for a candidate, not fixed compatibility
guarantees. Commit metadata and the isolated gate tie first-party binaries and
tests to the selected committed source snapshot. They do not make the build
byte-for-byte reproducible, cryptographically attest the SDK or restored package
contents, or form a package integrity manifest. The verifier does not launch the
application, create such a manifest, or prove code-signing status.

This workflow does not create an installer and does not sign the executable.
Windows reputation or publisher warnings therefore remain possible. Do not
describe the folder as installed, signed, or publicly released.

## Smoke-test evidence boundaries

Package verification is structural; it does not launch the UI. Record runtime
evidence separately and describe only what was directly observed:

- Successful process creation proves that Windows could start the packaged
  executable on that host.
- A bounded check that the process remains alive and responsive proves only that
  it did not immediately terminate or stop responding during that interval.
- Those checks do not prove visual layout, dialogs, configuration workflows,
  watcher behavior, long-running stability, or behavior on another Windows
  version.
- Interactive visual checks must be reported as **Not Tested** unless a person or
  supported UI automation actually observed them.
- Use only isolated scratch configuration and watched directories for smoke
  tests. Do not use Desktop, Documents, Downloads, or other real user folders.

The current desktop surface is configuration and read-only observation. Release
smoke testing must not be treated as authorization to expose or invoke the
physical execution pipeline.

## Release acceptance checklist

- [ ] The intended commit and diff were reviewed; the working tree contains no
      unrelated changes.
- [ ] The tracked and non-ignored untracked working tree is clean and the exact
      full commit intended for the package is recorded.
- [ ] Any desired local preflight restore/build/tests completed; these are
      feedback only and are not substituted for the isolated release gate.
- [ ] A new absent direct-child path under `artifacts\portable` was selected.
- [ ] `Publish-WindowsPortable.ps1` completed its isolated exact-commit restore,
      Release build, full tests, publish, exact published-App boundary scan, and
      verification successfully.
- [ ] `Test-WindowsPortablePackage.ps1` completed successfully for the exact
      candidate directory.
- [ ] The verifier reported the exact expected commit, `win-x64`,
      `SelfContained = True`, and `DiagnosticsEnabled = False`.
- [ ] The candidate contains no unexpected source, symbol, secret-bearing, or
      repository files.
- [ ] Runtime smoke evidence is recorded with its exact host and observation
      boundaries; unperformed visual checks are marked **Not Tested**.
- [ ] `git status` and the mutation-boundary audit were reviewed after packaging.
- [ ] No installer, signing, upload, Git remote, or external publication action
      was performed by this workflow.

## Cleanup

Generated release output is disposable and Git-ignored. Cleanup is limited to
the exact reviewed candidate directory, never the repository root or a broad
wildcard. The only release-related cleanup locations are:

- `artifacts\portable\win-x64`
- `artifacts\portable\<candidate-name>`
- `artifacts\portable\.FileOrganizer-staging-<exact-guid>` only when an exact
  staging-cleanup warning reports that it remains
- `artifacts\publish-probe-win-x64`
- `%TEMP%\FileOrganizer-release-<exact-guid>` only when an exact isolated-workspace
  cleanup warning reports that it remains

Before removing one, resolve and inspect its absolute path, confirm it remains
beneath the repository's `artifacts` directory, and confirm it is the intended
candidate. Keep an accepted candidate until any required checksum, archive, or
manual transfer is performed by a separately authorized release step.

# FileOrganizer safety model

This document describes the safety properties implemented by the current codebase. It distinguishes the desktop application's reachable behavior from the journaled execution backend that exists in the repository but is not connected to the desktop UI.

## Current desktop application boundary

The watched-file workflow exposed by `FileOrganizer.App` is read-only. The application composes `WindowsWatchPreviewPipeline`, displays the latest immutable planning preview for each root as a historical observation, and exposes pause, resume, failed-source reconnect, and baseline-retry controls. It does not construct `WindowsExecutionPipeline`, call `OrganizationPlanExecutor.ExecuteAsync` or `RecoverAsync`, acquire the global execution lease, create execution journals, or invoke the native mover. There is no execute or recovery command in the current UI.

The application is not globally filesystem-read-only: its explicit Open/New/Reload/Validate/Save/Revert/Import/Export configuration commands can read or atomically publish a caller-selected JSON configuration document, using a same-directory staging artifact and creating missing parent directories where needed. The selected configuration path is not prohibited from being inside a watched root. This mutation boundary is the explicitly selected configuration destination, its missing parent directories, and its staging artifact; these commands do not invoke the organization execution pipeline.

Configuration creation is no-overwrite, while Save and Import carry the exact byte revision
captured by the last read or publication. An external edit observed before final validation
stops with a structured conflict. Export refuses an existing path that is physically identical
to the current or active configuration, and an unprovable Windows identity fails closed.
Participating FileOrganizer writers in the same Windows login session hold one fixed named
mutex across validation and publication, so alternate path spellings do not split the writer
lock. Acquisition is bounded and an abandoned owner forces fresh validation.

The revision check is not an atomic compare-and-replace primitive. A writer outside the named
mutex protocol, including one in another Windows session, can still race the final managed-API
window whether the writer is benign or malicious.

The repository also contains a complete move-only execution backend for future explicitly authorized integration. Merely referencing the Infrastructure assembly does not make that backend reachable from the current application composition.

Preview-runtime replacement is staged: a candidate runtime is created and started before it
becomes visible, then the reference and immutable snapshot are swapped together. Creation,
startup, or cancellation failure disposes the candidate and preserves the previous active
runtime and snapshot. A late callback from either generation is fenced by runtime identity
and snapshot revision.

## Watch notifications are hints, not truth

`FileSystemWatcher` notifications are translated into immutable Application events. A notification is never treated as proof that a file is ready or that a watched root is complete.

Each enabled root begins in `RequiresInitialScan`. Starting its physical watcher does not make it synchronized. A full read-only baseline scan captures a generation token; the monitor accepts the baseline only if no watcher event arrived after that token was captured. Only an accepted baseline changes the state to `Synchronized`.

The trust states have these meanings:

- `RequiresInitialScan`: the physical watcher may be open, but incremental events are not trusted until an unchanged full baseline is accepted.
- `Synchronized`: incremental hints may enter debounce and stability processing, and a snapshot token may be issued for read-only preview work.
- `Paused`: the physical watcher is disposed and pending tracking is discarded. Events are not buffered for later use.
- `RescanRequired`: incremental knowledge is invalid. No trusted incremental candidate is emitted until a new full baseline is accepted.
- `Faulted` or `Unavailable`: the source cannot currently provide a trusted incremental stream.

Pausing clears pending candidates. Resuming opens a new physical watcher generation and enters `RescanRequired`; it does not assume that the paused interval was quiet.

A `Faulted` or `Unavailable` root exposes an explicit **Reconnect watcher** command. It
discards the failed source, opens a fresh source generation, and still enters
`RescanRequired`; only a later accepted baseline can restore `Synchronized`. Reconnect is
rejected for every non-failure state, disabled root, unknown root, or disposed runtime.

A native watcher-buffer overflow, the bounded adapter queue filling, or the bounded Application watcher pipeline filling invalidates incremental state. The monitor clears tracked work, records a structured diagnostic, and requires a new baseline. Watcher failures become `Faulted` or `Unavailable` according to their structured cause. Ambiguous directory-scope changes and unprovable rename/delete cases also prefer `RescanRequired` over reconstructing state by guesswork.

While a root is not `Synchronized`, ordinary created, changed, deleted, and renamed notifications cannot produce trusted stable candidates. Snapshot generation and pipeline epochs also fence stale asynchronous work after pause, rescan, watcher replacement, or disposal.

## Debounce, stability, and preview

For the current desktop composition, repeated path notifications are debounced for 750 milliseconds and the file is then required to remain stable across a two-second interval. These values are implementation defaults, not a claim about every writer's completion behavior.

A stable candidate is emitted only for a regular, non-reparse file whose strong physical identity, length, and last-write time remain unchanged across the stability observations. The candidate retains creation time when the filesystem supplies it. Before a candidate becomes a preview, the full-root scan must match its identity, length, creation time, and last-write time. A later notification, identity drift, or metadata drift restarts or invalidates the pending version. Missing, inaccessible, reparse, unsupported, or out-of-scope observations produce structured diagnostics or resynchronization rather than a candidate.

The preview coordinator performs read-only scanning, destination inspection, and planning. It publishes an immutable `WatchRootPreviewResult` only while the captured synchronized snapshot remains current. A ready preview is historical information:

> A preview is never authorization to execute its `OrganizationPlan`.

No future execution boundary may trust a preview's age, paths, identities, metadata, destination absence, or watcher state. Those facts must be re-established at that privileged boundary.

## Available move-only execution backend

The unbound backend accepts only an immutable, complete, execution-eligible plan that can compile into a deterministic `ExecutionManifest`. Every executable `Ready` item must be a `MoveToDirectoryAction` and must carry the required strong source-root, source-file, and destination-directory identities. A `Ready` Copy or literal Rename action rejects the whole manifest before journal creation. Delete has no execution path.

When `OrganizationPlanExecutor.ExecuteAsync` is invoked by an authorized host, the implemented order is:

1. Compile and validate the move-only manifest.
2. Acquire the genuine global Windows execution lease.
3. Re-read and validate durable journal state while holding the lease.
4. Replay an existing execution or stop for recovery instead of starting a duplicate.
5. Run read-only physical preflight for a genuinely new execution.
6. Durably publish the execution genesis.
7. For each manifest item in deterministic order, acquire and revalidate its physical move capability.
8. Durably publish `MoveIntent`.
9. Invoke the native move exactly once.
10. Reconcile source and target observations by strong physical identity and durably publish `MoveApplied`, `MoveNotApplied`, `MoveConflict`, `MoveAmbiguous`, or manual-recovery state.
11. Publish `ExecutionCompleted` only after every item is durably proven applied.

The physical adapter revalidates source identity and metadata, source-root and destination-directory identity, lexical containment, held ancestry, entry type/reparse state, same-volume placement, and target absence. Factory-composed execution also rejects a source root, destination directory, or target that overlaps the application-owned journal namespace before journal genesis. The adapter repeats safety-critical checks at commit time. A failed or uncertain check blocks mutation; it does not select a weaker fallback.

## Intent-before-mutation and durable outcomes

`MoveIntent` publication is the mandatory durability boundary immediately before user-file mutation. If intent publication fails, `CommitAsync` is not called and that item performs no physical mutation.

After the native call, the executor does not infer success from the return code alone. It observes both source and target and classifies the expected physical identity. Only proof that the expected identity is at the target becomes `Applied`. Definite non-application, a destination conflict, ambiguity, and inspection failure retain distinct result and journal states.

If publishing the reconciled result fails after the native call, execution stops with the durable intent still pending. The move is not retried and later items are not attempted. If publishing the final completion record fails, already committed moves remain committed, the run is not reported as durably completed, and a later `ExecuteAsync` invocation does not move the files again.

Journal publication uses same-directory temporary files, flushes file contents, and publishes immutable genesis/record entries without replacement. Records have fixed contiguous sequence numbers, execution ownership, payload integrity hashes, and a predecessor hash chain. Corrupt, conflicting, unsafe, or structurally invalid journal state fails closed. The implementation does not claim that a directory entry survives every sudden-power-loss scenario because .NET exposes no portable directory-flush operation.

## Fail-stop, replay, and recovery

The execution batch is not transactional. On the first blocked, conflicting, non-applied, ambiguous, persistence-failed, or otherwise unresolved item, later items remain `NotAttempted`. Earlier proven moves remain applied; there is no compensating move or automatic rollback.

A completed journal is replayed as completed with zero new move attempts. An incomplete, ambiguous, corrupt, or conflicting journal does not cause `ExecuteAsync` to resume or retry physical mutation. It returns a structured stop or recovery-required result.

`RecoverAsync` is a separate explicit boundary. It acquires the global lease and may append a reconciliation or missing completion record, but it uses read-only physical observations: it never acquires a move lease and never invokes `CommitAsync`. If an intent cannot be classified as applied, not applied, or conflict from the observed identities, it remains ambiguous or requires manual recovery. Recovery does not automatically resume later manifest items and does not roll back earlier ones.

Cancellation is honored at safe boundaries. Once an intent exists, cancellation cannot hide a physical outcome: the executor either records definite non-application before a commit starts or completes post-call reconciliation, records the observed outcome, and then stops before another item.

## Windows no-overwrite primitive and its limit

The only production primitive in the repository that mutates an organized user file is `SetFileInformationByHandle(FileRenameInfo)` in `WindowsNoOverwriteRenameNative`. It operates on an open source handle, supplies an absolute target path, and sets `FILE_RENAME_INFO.ReplaceIfExists` to false. Existing-target native errors are classified as conflicts; overwrite is not enabled. Cross-volume moves are blocked, and there is no copy/delete fallback.

The adapter opens relevant entries without following the final reparse point, holds source and destination ancestry handles, checks strong identities, rejects observed reparse points, and places a final no-follow target-absence check immediately before the native call. These checks materially narrow the race window but do not eliminate it.

The absolute target path is not resolved relative to a held destination-directory handle. Therefore the implementation does **not** guarantee a fully handle-bound or race-free destination against a separately authorized process that changes destination reparse resolution after final validation and before or during the native rename. That is the precise known limitation. Post-operation identity reconciliation can classify what is observable afterward, but it cannot turn this absolute-path commit into a handle-relative primitive.

## Explicitly unsupported execution behavior

The current safety boundary does not provide:

- Copy execution.
- Literal Rename execution.
- Delete execution.
- Overwrite or replacement.
- Cross-volume copy/delete emulation.
- Destination-directory creation.
- Automatic retry of a physical move.
- Automatic resume of later operations.
- Automatic rollback, compensation, or undo.
- Automatic execution from watcher events or previews.

Copy and literal Rename can exist in configuration and planning previews. Serialization or preview support does not make them physically executable. Any future UI integration must preserve the independent execution authorization and revalidation boundary described above; wiring a preview object directly to the mover would violate this safety model.

using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Application.Recovery;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Infrastructure.Journaling;
using Microsoft.Win32.SafeHandles;
using static FileOrganizer.Infrastructure.FileSystem.WindowsFileIdentityNative;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// Revalidates and performs one same-volume Windows no-overwrite move.
/// </summary>
/// <remarks>
/// The commit uses a source handle and an absolute target with
/// <c>FILE_RENAME_INFO.ReplaceIfExists</c> set to false. It is deliberately not
/// described as fully handle-bound or race-free: a separately authorized
/// process may alter destination path resolution through a reparse-point
/// mutation after the final validation and before or during the absolute-path
/// rename. The adapter still holds the execution capability and relevant
/// handles, rejects observed reparse points, and requires fresh reconciliation.
/// </remarks>
public sealed class WindowsPhysicalMoveAdapter : IMoveOperationPort
{
    private const uint DeleteAccess = 0x00010000;
    private const int ErrorNotSameDevice = 17;

    private const uint DirectoryShareMode = FileShareRead | FileShareWrite;
    private const uint SourceShareMode = FileShareRead;
    private const uint NoFollowFlags = FileFlagBackupSemantics | FileFlagOpenReparsePoint;

    private readonly bool _isWindows;
    private readonly Func<CanonicalPath, CancellationToken, PhysicalEntryObservation> _observePath;
    private readonly Func<SafeFileHandle, WindowsRenameBuffer, WindowsRenameResult> _rename;

    /// <summary>Creates the production Windows physical move adapter.</summary>
    public WindowsPhysicalMoveAdapter()
        : this(
            OperatingSystem.IsWindows(),
            new WindowsPhysicalIdentityAdapter().Observe,
            WindowsNoOverwriteRenameNative.Rename)
    {
    }

    internal WindowsPhysicalMoveAdapter(
        bool isWindows,
        Func<CanonicalPath, CancellationToken, PhysicalEntryObservation> observePath,
        Func<SafeFileHandle, WindowsRenameBuffer, WindowsRenameResult> rename)
    {
        ArgumentNullException.ThrowIfNull(observePath);
        ArgumentNullException.ThrowIfNull(rename);

        _isWindows = isWindows;
        _observePath = observePath;
        _rename = rename;
    }

    /// <inheritdoc />
    public ValueTask<MoveLeaseAcquisition> TryAcquireAsync(
        IGlobalExecutionLease executionLease,
        ExecutionManifest manifest,
        ExecutionManifestItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        if (!WindowsGlobalExecutionLeaseProvider.TryBorrow(executionLease, out var borrow))
        {
            throw new InvalidOperationException(
                "A genuine, live Windows execution lease is required before filesystem access.");
        }

        try
        {
            if (!_isWindows)
            {
                return ValueTask.FromResult(BlockDestination(
                    item,
                    "The Windows physical move capability is unavailable on this host."));
            }

            MoveLeaseAcquisition acquisition = Acquire(
                manifest,
                item,
                borrow,
                cancellationToken);

            if (acquisition.Readiness == ExecutionReadiness.Ready)
            {
                borrow = null;
            }

            return ValueTask.FromResult(acquisition);
        }
        finally
        {
            borrow?.Dispose();
        }
    }

    private MoveLeaseAcquisition Acquire(
        ExecutionManifest manifest,
        ExecutionManifestItem item,
        IWindowsExecutionLeaseBorrow borrow,
        CancellationToken cancellationToken)
    {
        MoveLeaseAcquisition? pathFailure = ValidateManifestPaths(manifest, item);
        if (pathFailure is not null)
        {
            return pathFailure;
        }

        var pathPolicy = new PathPolicy(
            manifest.PathSemantics.Syntax,
            manifest.PathSemantics.CaseSensitivity,
            manifest.PathSemantics.UnicodeNormalization);
        var directories = new List<HeldDirectory>();
        SafeFileHandle? sourceHandle = null;
        WindowsRenameBuffer? renameBuffer = null;
        bool transferred = false;

        try
        {
            CanonicalPath sourceParent = pathPolicy.GetParent(
                item.SourcePrecondition.CanonicalPath)!;

            MoveLeaseAcquisition? failure = OpenDirectoryChain(
                sourceParent,
                manifest.SourceRoot,
                manifest.SourceRoot,
                manifest.SourceRootPhysicalIdentity,
                item,
                isSourceBoundary: true,
                directories,
                cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            failure = OpenDirectoryChain(
                item.DestinationDirectory,
                GetLexicalRoot(pathPolicy, item.DestinationDirectory),
                item.DestinationDirectory,
                item.DestinationDirectoryPhysicalIdentity,
                item,
                isSourceBoundary: false,
                directories,
                cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            cancellationToken.ThrowIfCancellationRequested();
            sourceHandle = OpenHandle(
                item.SourcePrecondition.CanonicalPath.Value,
                DeleteAccess | FileReadAttributes,
                SourceShareMode);
            if (sourceHandle.IsInvalid)
            {
                int errorCode = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                return BlockSource(
                    item,
                    DescribeObservationFailure(
                        "The source could not be opened for a handle-bound move.",
                        WindowsPhysicalIdentityAdapter.FromOpenFailure(errorCode)));
            }

            PhysicalEntryObservation source = WindowsPhysicalIdentityAdapter.ObserveHandle(
                sourceHandle,
                cancellationToken);
            failure = ValidateSourceObservation(item, source);
            if (failure is not null)
            {
                return failure;
            }

            PhysicalEntryIdentity sourceIdentity = source.Identity!;
            failure = ValidatePhysicalVolumes(
                item,
                sourceIdentity,
                manifest.SourceRootPhysicalIdentity,
                item.DestinationDirectoryPhysicalIdentity);
            if (failure is not null)
            {
                return failure;
            }

            TargetCheck target = CheckTarget(item.TargetPath, cancellationToken);
            if (target == TargetCheck.Exists)
            {
                return Conflict(item, "The no-overwrite target already exists.");
            }

            if (target == TargetCheck.Unsafe)
            {
                return BlockDestination(
                    item,
                    "The no-overwrite target could not be inspected safely.");
            }

            renameBuffer = new WindowsRenameBuffer(item.TargetPath.Value);
            cancellationToken.ThrowIfCancellationRequested();

            var lease = new PhysicalMoveLease(
                manifest,
                item,
                pathPolicy,
                directories,
                sourceHandle,
                sourceIdentity,
                borrow,
                renameBuffer,
                _observePath,
                _rename);

            transferred = true;
            sourceHandle = null;
            renameBuffer = null;
            return MoveLeaseAcquisition.Ready(lease);
        }
        finally
        {
            if (!transferred)
            {
                renameBuffer?.Dispose();
                sourceHandle?.Dispose();
                DisposeDirectories(directories);
            }
        }
    }

    private static MoveLeaseAcquisition? ValidateManifestPaths(
        ExecutionManifest manifest,
        ExecutionManifestItem item)
    {
        if (manifest.PathSemantics.Syntax != PathSyntax.Windows)
        {
            return BlockDestination(
                item,
                "The Windows move adapter requires explicit Windows path semantics.");
        }

        if (item.Ordinal >= manifest.Items.Count
            || !ReferenceEquals(manifest.Items[item.Ordinal], item))
        {
            throw new ArgumentException(
                "The move item must be the item at its ordinal in the supplied manifest.",
                nameof(item));
        }

        var policy = new PathPolicy(
            manifest.PathSemantics.Syntax,
            manifest.PathSemantics.CaseSensitivity,
            manifest.PathSemantics.UnicodeNormalization);

        if (policy.Relate(manifest.SourceRoot, item.SourcePrecondition.CanonicalPath)
            != PathRelationship.Descendant)
        {
            return BlockSource(
                item,
                "The source is not canonically contained by the manifest source root.");
        }

        CanonicalPath? targetParent = policy.GetParent(item.TargetPath);
        if (targetParent is null
            || !policy.AreEquivalent(targetParent, item.DestinationDirectory)
            || policy.Relate(item.DestinationDirectory, item.TargetPath)
                != PathRelationship.Descendant)
        {
            return BlockDestination(
                item,
                "The target is not one direct file child of the destination directory.");
        }

        if (policy.AreEquivalent(item.SourcePrecondition.CanonicalPath, item.TargetPath))
        {
            return BlockDestination(
                item,
                "The source and target are the same logical path.");
        }

        return null;
    }

    private static MoveLeaseAcquisition? OpenDirectoryChain(
        CanonicalPath finalDirectory,
        CanonicalPath firstDirectory,
        CanonicalPath identityPath,
        PhysicalEntryIdentity expectedIdentity,
        ExecutionManifestItem item,
        bool isSourceBoundary,
        List<HeldDirectory> destinations,
        CancellationToken cancellationToken)
    {
        var policy = new PathPolicy(
            finalDirectory.Semantics.Syntax,
            finalDirectory.Semantics.CaseSensitivity,
            finalDirectory.Semantics.UnicodeNormalization);
        var reverse = new Stack<CanonicalPath>();
        CanonicalPath? current = finalDirectory;
        while (current is not null)
        {
            reverse.Push(current);
            if (policy.AreEquivalent(current, firstDirectory))
            {
                break;
            }

            current = policy.GetParent(current);
        }

        if (current is null)
        {
            return isSourceBoundary
                ? BlockSource(item, "The source root was not present in the source path chain.")
                : BlockDestination(item, "The destination directory was not validated.");
        }

        bool foundIdentityPath = false;
        while (reverse.TryPop(out CanonicalPath? path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle handle = OpenHandle(
                path.Value,
                FileReadAttributes,
                DirectoryShareMode);
            if (handle.IsInvalid)
            {
                int errorCode = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                handle.Dispose();
                return isSourceBoundary
                    ? BlockSource(
                        item,
                        DescribeObservationFailure(
                            "A source directory component could not be opened safely.",
                            WindowsPhysicalIdentityAdapter.FromOpenFailure(errorCode)))
                    : BlockDestination(
                        item,
                        DescribeObservationFailure(
                            "A destination directory component could not be opened safely.",
                            WindowsPhysicalIdentityAdapter.FromOpenFailure(errorCode)));
            }

            PhysicalEntryObservation observation;
            try
            {
                observation = WindowsPhysicalIdentityAdapter.ObserveHandle(
                    handle,
                    cancellationToken);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            if (observation.Outcome != PhysicalEntryObservationOutcome.Success
                || observation.Identity!.EntryKind != PhysicalEntryKind.Directory)
            {
                handle.Dispose();
                return isSourceBoundary
                    ? BlockSource(
                        item,
                        DescribeObservationFailure(
                            "A source directory component is missing, changed, or unsupported.",
                            observation))
                    : BlockDestination(
                        item,
                        DescribeObservationFailure(
                            "A destination directory component is missing, changed, or unsupported.",
                            observation));
            }

            bool isIdentityPath = policy.AreEquivalent(path, identityPath);
            if (isIdentityPath && !observation.Identity.Equals(expectedIdentity))
            {
                handle.Dispose();
                return isSourceBoundary
                    ? BlockSource(item, "The source-root physical identity changed.")
                    : BlockDestination(item, "The destination-directory physical identity changed.");
            }

            foundIdentityPath |= isIdentityPath;
            destinations.Add(
                new HeldDirectory(
                    handle,
                    observation.Identity,
                    isSourceBoundary));
        }

        if (!foundIdentityPath)
        {
            return isSourceBoundary
                ? BlockSource(item, "The source root was not present in the source path chain.")
                : BlockDestination(item, "The destination directory was not validated.");
        }

        return null;
    }

    private static CanonicalPath GetLexicalRoot(
        PathPolicy policy,
        CanonicalPath path)
    {
        CanonicalPath root = path;
        while (policy.GetParent(root) is { } parent)
        {
            root = parent;
        }

        return root;
    }

    private static MoveLeaseAcquisition? ValidateSourceObservation(
        ExecutionManifestItem item,
        PhysicalEntryObservation observation)
    {
        if (observation.Outcome != PhysicalEntryObservationOutcome.Success
            || observation.Identity!.EntryKind != PhysicalEntryKind.RegularFile
            || observation.FileMetadata is null)
        {
            return BlockSource(
                item,
                DescribeObservationFailure(
                    "The source is missing, changed, a reparse point, or unsupported.",
                    observation));
        }

        if (!observation.Identity.Equals(item.SourcePrecondition.PhysicalIdentity))
        {
            return BlockSource(item, "The source physical identity changed.");
        }

        PhysicalFileMetadata metadata = observation.FileMetadata;
        if (metadata.Length != item.SourcePrecondition.Length
            || metadata.ModifiedUtc != item.SourcePrecondition.ModifiedUtc
            || (item.SourcePrecondition.CreatedUtc.HasValue
                && metadata.CreatedUtc != item.SourcePrecondition.CreatedUtc))
        {
            return BlockSource(item, "The source metadata changed after planning.");
        }

        return null;
    }

    private TargetCheck CheckTarget(
        CanonicalPath targetPath,
        CancellationToken cancellationToken)
    {
        PhysicalEntryObservation observation = _observePath(targetPath, cancellationToken);
        return ClassifyTarget(observation);
    }

    private static TargetCheck ClassifyTarget(PhysicalEntryObservation observation)
    {
        if (observation.Outcome == PhysicalEntryObservationOutcome.Missing
            && observation.NativeErrorCode == 2)
        {
            return TargetCheck.Missing;
        }

        if (observation.Outcome == PhysicalEntryObservationOutcome.Success
            || observation.Reason is
                PhysicalEntryObservationReason.ReparsePoint
                or PhysicalEntryObservationReason.UnsupportedEntryKind
                or PhysicalEntryObservationReason.DeletePending
                or PhysicalEntryObservationReason.MetadataUnavailable)
        {
            return TargetCheck.Exists;
        }

        return TargetCheck.Unsafe;
    }

    private static SafeFileHandle OpenHandle(
        string path,
        uint desiredAccess,
        uint shareMode) => CreateFile(
            path,
            desiredAccess,
            shareMode,
            0,
            OpenExisting,
            NoFollowFlags,
            0);

    internal static bool SamePhysicalVolume(
        PhysicalEntryIdentity left,
        PhysicalEntryIdentity right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.SchemeVersion == right.SchemeVersion
            && StringComparer.Ordinal.Equals(left.Scheme, right.Scheme)
            && StringComparer.Ordinal.Equals(left.VolumeIdentity, right.VolumeIdentity);
    }

    internal static MoveLeaseAcquisition? ValidatePhysicalVolumes(
        ExecutionManifestItem item,
        PhysicalEntryIdentity source,
        PhysicalEntryIdentity sourceRoot,
        PhysicalEntryIdentity destination)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(destination);

        if (!SamePhysicalVolume(source, sourceRoot))
        {
            return BlockSource(
                item,
                "The source and declared source root no longer resolve to the same physical volume.");
        }

        return SamePhysicalVolume(source, destination)
            ? null
            : BlockDestination(
                item,
                "The source and destination directory are on different physical volumes.");
    }

    private static RecoveryEntryObservation ToRecoveryObservation(
        PhysicalEntryObservation observation) => observation.Outcome switch
        {
            PhysicalEntryObservationOutcome.Success =>
                RecoveryEntryObservation.Present(observation.Identity!),
            PhysicalEntryObservationOutcome.Missing => RecoveryEntryObservation.Missing(),
            PhysicalEntryObservationOutcome.Inaccessible
                or PhysicalEntryObservationOutcome.Unsupported =>
                RecoveryEntryObservation.InspectionFailed(ToRecoveryFailure(observation)),
            _ => throw new InvalidOperationException("Unknown physical observation outcome."),
        };

    private static RecoveryInspectionFailureKind ToRecoveryFailure(
        PhysicalEntryObservation observation) => observation.Reason switch
        {
            PhysicalEntryObservationReason.AccessDenied =>
                RecoveryInspectionFailureKind.AccessDenied,
            PhysicalEntryObservationReason.InvalidPath =>
                RecoveryInspectionFailureKind.InvalidPath,
            PhysicalEntryObservationReason.ReparsePoint
                or PhysicalEntryObservationReason.UnsupportedCapability
                or PhysicalEntryObservationReason.UnsupportedEntryKind
                or PhysicalEntryObservationReason.DeletePending
                or PhysicalEntryObservationReason.MetadataUnavailable =>
                RecoveryInspectionFailureKind.UnsupportedEntry,
            PhysicalEntryObservationReason.EntryChanged =>
                RecoveryInspectionFailureKind.EntryChanged,
            _ => RecoveryInspectionFailureKind.IoFailure,
        };

    private static string DescribeObservationFailure(
        string prefix,
        PhysicalEntryObservation observation) =>
        $"{prefix} Outcome={observation.Outcome}; Reason={observation.Reason}.";

    private static MoveLeaseAcquisition BlockSource(
        ExecutionManifestItem item,
        string description) => MoveLeaseAcquisition.Blocked(
            SourceProblem(item, description));

    private static MoveLeaseAcquisition BlockDestination(
        ExecutionManifestItem item,
        string description) => MoveLeaseAcquisition.Blocked(
            DestinationProblem(item, description));

    private static MoveLeaseAcquisition Conflict(
        ExecutionManifestItem item,
        string description) => MoveLeaseAcquisition.Conflict(
            new ExecutionProblem(
                ExecutionProblemKind.DestinationConflict,
                description,
                item.Ordinal));

    private static ExecutionProblem SourceProblem(
        ExecutionManifestItem item,
        string description) => new(
            ExecutionProblemKind.SourceRevalidationFailed,
            description,
            item.Ordinal);

    private static ExecutionProblem DestinationProblem(
        ExecutionManifestItem item,
        string description) => new(
            ExecutionProblemKind.DestinationRevalidationFailed,
            description,
            item.Ordinal);

    private static void DisposeDirectories(List<HeldDirectory> directories)
    {
        for (int index = directories.Count - 1; index >= 0; index--)
        {
            directories[index].Dispose();
        }

        directories.Clear();
    }

    private enum TargetCheck
    {
        Missing,
        Exists,
        Unsafe,
    }

    private sealed class HeldDirectory : IDisposable
    {
        internal HeldDirectory(
            SafeFileHandle handle,
            PhysicalEntryIdentity identity,
            bool isSourceBoundary)
        {
            Handle = handle;
            Identity = identity;
            IsSourceBoundary = isSourceBoundary;
        }

        internal SafeFileHandle Handle { get; }

        internal PhysicalEntryIdentity Identity { get; }

        internal bool IsSourceBoundary { get; }

        public void Dispose() => Handle.Dispose();
    }

    private sealed class PhysicalMoveLease : IMoveOperationLease
    {
        private readonly object _stateLock = new();
        private readonly ExecutionManifest _manifest;
        private readonly ExecutionManifestItem _item;
        private readonly PathPolicy _pathPolicy;
        private readonly List<HeldDirectory> _directories;
        private readonly SafeFileHandle _sourceHandle;
        private readonly IWindowsExecutionLeaseBorrow _executionBorrow;
        private readonly WindowsRenameBuffer _renameBuffer;
        private readonly Func<CanonicalPath, CancellationToken, PhysicalEntryObservation> _observePath;
        private readonly Func<SafeFileHandle, WindowsRenameBuffer, WindowsRenameResult> _rename;

        private MoveAttemptResult? _attempt;
        private bool _disposed;

        internal PhysicalMoveLease(
            ExecutionManifest manifest,
            ExecutionManifestItem item,
            PathPolicy pathPolicy,
            List<HeldDirectory> directories,
            SafeFileHandle sourceHandle,
            PhysicalEntryIdentity sourceIdentity,
            IWindowsExecutionLeaseBorrow executionBorrow,
            WindowsRenameBuffer renameBuffer,
            Func<CanonicalPath, CancellationToken, PhysicalEntryObservation> observePath,
            Func<SafeFileHandle, WindowsRenameBuffer, WindowsRenameResult> rename)
        {
            _manifest = manifest;
            _item = item;
            _pathPolicy = pathPolicy;
            _directories = directories;
            _sourceHandle = sourceHandle;
            _executionBorrow = executionBorrow;
            _renameBuffer = renameBuffer;
            _observePath = observePath;
            _rename = rename;

            SourcePhysicalIdentity = sourceIdentity;
            DestinationDirectoryPhysicalIdentity = item.DestinationDirectoryPhysicalIdentity;
        }

        public PhysicalEntryIdentity SourcePhysicalIdentity { get; }

        public PhysicalEntryIdentity DestinationDirectoryPhysicalIdentity { get; }

        public ValueTask<MoveAttemptResult> CommitAsync()
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _attempt ??= CommitCore();
                return ValueTask.FromResult(_attempt);
            }
        }

        public ValueTask<MoveObservation> ObserveAsync()
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_attempt is null)
                {
                    throw new InvalidOperationException(
                        "The move must be attempted before post-operation observation.");
                }

                PhysicalEntryObservation source = _observePath(
                    _item.SourcePrecondition.CanonicalPath,
                    CancellationToken.None);
                PhysicalEntryObservation target = _observePath(
                    _item.TargetPath,
                    CancellationToken.None);

                return ValueTask.FromResult(
                    new MoveObservation(
                        ToRecoveryObservation(source),
                        ToRecoveryObservation(target)));
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return ValueTask.CompletedTask;
                }

                _disposed = true;
                _renameBuffer.Dispose();
                _sourceHandle.Dispose();
                DisposeDirectories(_directories);
                _executionBorrow.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private MoveAttemptResult CommitCore()
        {
            MoveAttemptResult? sourceFailure = RevalidateSource();
            if (sourceFailure is not null)
            {
                return sourceFailure;
            }

            MoveAttemptResult? directoryFailure = RevalidateDirectories();
            if (directoryFailure is not null)
            {
                return directoryFailure;
            }

            if (!_pathPolicy.AreEquivalent(
                    _pathPolicy.GetParent(_item.TargetPath)!,
                    _item.DestinationDirectory)
                || _pathPolicy.AreEquivalent(
                    _item.SourcePrecondition.CanonicalPath,
                    _item.TargetPath))
            {
                return MoveAttemptResult.Blocked(
                    DestinationProblem(
                        _item,
                        "The target path no longer satisfies the manifest containment policy."));
            }

            PhysicalEntryObservation rootAtPath = _observePath(
                _manifest.SourceRoot,
                CancellationToken.None);
            if (rootAtPath.Outcome != PhysicalEntryObservationOutcome.Success
                || !rootAtPath.Identity!.Equals(_manifest.SourceRootPhysicalIdentity))
            {
                return MoveAttemptResult.Blocked(
                    SourceProblem(_item, "The source-root path or physical identity changed."));
            }

            PhysicalEntryObservation destinationAtPath = _observePath(
                _item.DestinationDirectory,
                CancellationToken.None);
            if (destinationAtPath.Outcome != PhysicalEntryObservationOutcome.Success
                || destinationAtPath.Identity!.EntryKind != PhysicalEntryKind.Directory
                || !destinationAtPath.Identity.Equals(
                    _item.DestinationDirectoryPhysicalIdentity))
            {
                return MoveAttemptResult.Blocked(
                    DestinationProblem(
                        _item,
                        "The destination path or physical identity changed."));
            }

            if (!SamePhysicalVolume(
                    SourcePhysicalIdentity,
                    destinationAtPath.Identity))
            {
                return MoveAttemptResult.Blocked(
                    DestinationProblem(
                        _item,
                        "The source and destination are not on the same physical volume."));
            }

            // Keep this final no-follow target check immediately adjacent to the
            // one mutation call. The rename buffer was allocated beforehand.
            TargetCheck target = ClassifyTarget(
                _observePath(_item.TargetPath, CancellationToken.None));
            if (target == TargetCheck.Exists)
            {
                return MoveAttemptResult.Conflict(
                    new ExecutionProblem(
                        ExecutionProblemKind.DestinationConflict,
                        "The target appeared at the native no-overwrite boundary.",
                        _item.Ordinal));
            }

            if (target == TargetCheck.Unsafe)
            {
                return MoveAttemptResult.Blocked(
                    DestinationProblem(
                        _item,
                        "The target could not be inspected at the native boundary."));
            }

            WindowsRenameResult nativeResult = _rename(_sourceHandle, _renameBuffer);
            if (nativeResult.Succeeded)
            {
                return MoveAttemptResult.Success();
            }

            if (nativeResult.ErrorCode is
                WindowsNoOverwriteRenameNative.ErrorFileExists
                or WindowsNoOverwriteRenameNative.ErrorAlreadyExists)
            {
                return MoveAttemptResult.Conflict(
                    new ExecutionProblem(
                        ExecutionProblemKind.DestinationConflict,
                        "The target appeared during the native no-overwrite rename.",
                        _item.Ordinal));
            }

            if (nativeResult.ErrorCode == ErrorNotSameDevice)
            {
                return MoveAttemptResult.Blocked(
                    DestinationProblem(
                        _item,
                        "Windows rejected the move because the target is on another volume."));
            }

            return MoveAttemptResult.Failure(
                new ExecutionProblem(
                    ExecutionProblemKind.MoveFailed,
                    $"The Windows no-overwrite rename failed with native error {nativeResult.ErrorCode}.",
                    _item.Ordinal));
        }

        private MoveAttemptResult? RevalidateSource()
        {
            PhysicalEntryObservation source = WindowsPhysicalIdentityAdapter.ObserveHandle(
                _sourceHandle,
                CancellationToken.None);
            MoveLeaseAcquisition? validation = ValidateSourceObservation(_item, source);
            return validation is null
                ? null
                : MoveAttemptResult.Blocked(validation.Problem!);
        }

        private MoveAttemptResult? RevalidateDirectories()
        {
            foreach (HeldDirectory directory in _directories)
            {
                PhysicalEntryObservation observation = WindowsPhysicalIdentityAdapter.ObserveHandle(
                    directory.Handle,
                    CancellationToken.None);
                if (observation.Outcome == PhysicalEntryObservationOutcome.Success
                    && observation.Identity!.EntryKind == PhysicalEntryKind.Directory
                    && observation.Identity.Equals(directory.Identity))
                {
                    continue;
                }

                ExecutionProblem problem = directory.IsSourceBoundary
                    ? SourceProblem(
                        _item,
                        "A held source directory component changed before commit.")
                    : DestinationProblem(
                        _item,
                        "A held destination directory component changed before commit.");
                return MoveAttemptResult.Blocked(problem);
            }

            return null;
        }
    }
}

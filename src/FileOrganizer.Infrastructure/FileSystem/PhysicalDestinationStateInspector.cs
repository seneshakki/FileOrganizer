using System.Security;
using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// Captures read-only existence, type, and physical-identity facts for the
/// exact destination paths requested by a prepared plan.
/// </summary>
public sealed class PhysicalDestinationStateInspector : IDestinationStateInspector
{
    private readonly IReadOnlyFileSystemProbe _probe;

    /// <summary>Creates the Windows physical destination inspector.</summary>
    public PhysicalDestinationStateInspector()
        : this(CreateWindowsProbe())
    {
    }

    internal PhysicalDestinationStateInspector(IReadOnlyFileSystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <inheritdoc />
    public DestinationStateSnapshot Inspect(
        OrganizationPlanPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        cancellationToken.ThrowIfCancellationRequested();

        PathSyntax hostSyntax = OperatingSystem.IsWindows()
            ? PathSyntax.Windows
            : PathSyntax.Posix;
        bool incompatibleSyntax = preparation.PathSemantics.Syntax != hostSyntax;
        List<DestinationPathFact> facts = new(preparation.DestinationPaths.Count);

        foreach (CanonicalPath path in preparation.DestinationPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (incompatibleSyntax || path.Semantics != preparation.PathSemantics)
            {
                facts.Add(
                    Failed(
                        path,
                        DestinationInspectionFailureKind.InvalidPath,
                        "The destination path syntax is incompatible with the host adapter."));
                continue;
            }

            facts.Add(InspectPath(path, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        DestinationStateSnapshot snapshot = new(
            preparation.PathSemantics,
            facts,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    private DestinationPathFact InspectPath(
        CanonicalPath path,
        CancellationToken cancellationToken)
    {
        PhysicalEntryObservation observation;
        try
        {
            observation = _probe.Observe(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (FileNotFoundException)
        {
            return MissingAfterCancellationCheck(path, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return MissingAfterCancellationCheck(path, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return FailureAfterCancellationCheck(
                path,
                DestinationInspectionFailureKind.AccessDenied,
                "Access to the destination path was denied.",
                cancellationToken);
        }
        catch (SecurityException)
        {
            return FailureAfterCancellationCheck(
                path,
                DestinationInspectionFailureKind.AccessDenied,
                "Access to the destination path was denied.",
                cancellationToken);
        }
        catch (PathTooLongException)
        {
            return InvalidPathAfterCancellationCheck(path, cancellationToken);
        }
        catch (ArgumentException)
        {
            return InvalidPathAfterCancellationCheck(path, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return InvalidPathAfterCancellationCheck(path, cancellationToken);
        }
        catch (IOException)
        {
            return FailureAfterCancellationCheck(
                path,
                DestinationInspectionFailureKind.InputOutputFailure,
                "An input/output failure prevented destination inspection.",
                cancellationToken);
        }

        return FromObservation(path, observation);
    }

    private static DestinationPathFact FromObservation(
        CanonicalPath path,
        PhysicalEntryObservation observation)
    {
        if (observation.Outcome == PhysicalEntryObservationOutcome.Success
            || (observation.Outcome == PhysicalEntryObservationOutcome.Unsupported
                && observation.Reason == PhysicalEntryObservationReason.UnsupportedCapability))
        {
            return observation.EntryKind switch
            {
                PhysicalEntryKind.RegularFile => new DestinationPathFact(
                    path,
                    DestinationEntryKind.File,
                    physicalIdentity: observation.Identity),
                PhysicalEntryKind.Directory => new DestinationPathFact(
                    path,
                    DestinationEntryKind.Directory,
                    physicalIdentity: observation.Identity),
                _ => Unsupported(path),
            };
        }

        if (observation.Outcome == PhysicalEntryObservationOutcome.Missing)
        {
            return new DestinationPathFact(path, DestinationEntryKind.Missing);
        }

        if (observation.Outcome == PhysicalEntryObservationOutcome.Inaccessible)
        {
            return observation.Reason switch
            {
                PhysicalEntryObservationReason.AccessDenied => Failed(
                    path,
                    DestinationInspectionFailureKind.AccessDenied,
                    "Access to the destination path was denied."),
                PhysicalEntryObservationReason.EntryChanged => Changed(path),
                _ => Failed(
                    path,
                    DestinationInspectionFailureKind.InputOutputFailure,
                    "A read-only handle query could not establish destination state."),
            };
        }

        return observation.Reason switch
        {
            PhysicalEntryObservationReason.ReparsePoint
                or PhysicalEntryObservationReason.UnsupportedEntryKind
                or PhysicalEntryObservationReason.UnsupportedCapability => Unsupported(path),
            PhysicalEntryObservationReason.DeletePending => Changed(path),
            PhysicalEntryObservationReason.InvalidPath => Failed(
                path,
                DestinationInspectionFailureKind.InvalidPath,
                "The platform rejected the destination path."),
            _ => Failed(
                path,
                DestinationInspectionFailureKind.InputOutputFailure,
                "Required destination metadata was unavailable."),
        };
    }

    private static DestinationPathFact MissingAfterCancellationCheck(
        CanonicalPath path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new DestinationPathFact(path, DestinationEntryKind.Missing);
    }

    private static DestinationPathFact InvalidPathAfterCancellationCheck(
        CanonicalPath path,
        CancellationToken cancellationToken) =>
        FailureAfterCancellationCheck(
            path,
            DestinationInspectionFailureKind.InvalidPath,
            "The platform rejected the destination path.",
            cancellationToken);

    private static DestinationPathFact FailureAfterCancellationCheck(
        CanonicalPath path,
        DestinationInspectionFailureKind kind,
        string description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Failed(path, kind, description);
    }

    private static DestinationPathFact Changed(CanonicalPath path) =>
        Failed(
            path,
            DestinationInspectionFailureKind.EntryChanged,
            "The destination entry changed while it was being inspected.");

    private static DestinationPathFact Unsupported(CanonicalPath path) =>
        Failed(
            path,
            DestinationInspectionFailureKind.UnsupportedEntry,
            "The destination is a link, reparse point, device, or unsupported entry type.");

    private static DestinationPathFact Failed(
        CanonicalPath path,
        DestinationInspectionFailureKind kind,
        string description) =>
        new(
            path,
            DestinationEntryKind.InspectionFailed,
            new DestinationInspectionFailure(kind, description));

    private static SystemReadOnlyFileSystemProbe CreateWindowsProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Physical destination inspection is Windows-only because a portable "
                + "strong physical identity adapter is not available for this host.");
        }

        return new SystemReadOnlyFileSystemProbe();
    }
}

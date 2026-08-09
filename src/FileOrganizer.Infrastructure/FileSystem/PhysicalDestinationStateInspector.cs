using System.Security;
using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// Captures read-only existence/type facts for the exact destination paths a
/// prepared plan requests.
/// </summary>
/// <remarks>
/// The public physical adapter is deliberately Windows-only in M3A for the
/// same entry-type safety reason as <see cref="PhysicalFileScanner"/>. It reads
/// attributes twice for supported entries, detecting an observable type/removal
/// race, but this is not a durable identity guarantee. M3B must revalidate
/// immediately before execution.
/// </remarks>
public sealed class PhysicalDestinationStateInspector : IDestinationStateInspector
{
    private const FileAttributes UnsupportedAttributes =
        FileAttributes.ReparsePoint | FileAttributes.Device;

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
        AttributeObservation first = ReadAttributes(
            path,
            missingMeansChanged: false,
            cancellationToken);

        if (first.Fact is not null)
        {
            return first.Fact;
        }

        DestinationEntryKind firstKind = Classify(first.Attributes);
        if (firstKind == DestinationEntryKind.InspectionFailed)
        {
            return Unsupported(path);
        }

        AttributeObservation second = ReadAttributes(
            path,
            missingMeansChanged: true,
            cancellationToken);

        if (second.Fact is not null)
        {
            return second.Fact;
        }

        DestinationEntryKind secondKind = Classify(second.Attributes);
        if (secondKind == DestinationEntryKind.InspectionFailed)
        {
            return Unsupported(path);
        }

        if (firstKind != secondKind)
        {
            return Failed(
                path,
                DestinationInspectionFailureKind.EntryChanged,
                "The destination entry changed type while it was being inspected.");
        }

        return new DestinationPathFact(path, secondKind);
    }

    private AttributeObservation ReadAttributes(
        CanonicalPath path,
        bool missingMeansChanged,
        CancellationToken cancellationToken)
    {
        try
        {
            FileAttributes attributes = _probe.GetAttributes(path.Value);
            cancellationToken.ThrowIfCancellationRequested();
            return new AttributeObservation(attributes, null);
        }
        catch (FileNotFoundException)
        {
            return MissingOrChanged(path, missingMeansChanged, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return MissingOrChanged(path, missingMeansChanged, cancellationToken);
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
            return FailureAfterCancellationCheck(
                path,
                DestinationInspectionFailureKind.InvalidPath,
                "The platform rejected the destination path.",
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return FailureAfterCancellationCheck(
                path,
                DestinationInspectionFailureKind.InvalidPath,
                "The platform rejected the destination path.",
                cancellationToken);
        }
        catch (NotSupportedException)
        {
            return FailureAfterCancellationCheck(
                path,
                DestinationInspectionFailureKind.InvalidPath,
                "The platform rejected the destination path.",
                cancellationToken);
        }
        catch (IOException)
        {
            return FailureAfterCancellationCheck(
                path,
                DestinationInspectionFailureKind.InputOutputFailure,
                "An input/output failure prevented destination inspection.",
                cancellationToken);
        }
    }

    private static AttributeObservation MissingOrChanged(
        CanonicalPath path,
        bool missingMeansChanged,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DestinationPathFact fact = missingMeansChanged
            ? Failed(
                path,
                DestinationInspectionFailureKind.EntryChanged,
                "The destination entry disappeared while it was being inspected.")
            : new DestinationPathFact(path, DestinationEntryKind.Missing);

        return new AttributeObservation(default, fact);
    }

    private static AttributeObservation FailureAfterCancellationCheck(
        CanonicalPath path,
        DestinationInspectionFailureKind kind,
        string description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new AttributeObservation(default, Failed(path, kind, description));
    }

    private static DestinationEntryKind Classify(FileAttributes attributes) =>
        (attributes & UnsupportedAttributes) != 0
            ? DestinationEntryKind.InspectionFailed
            : (attributes & FileAttributes.Directory) != 0
                ? DestinationEntryKind.Directory
                : DestinationEntryKind.File;

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
                "Physical destination inspection is Windows-only in M3A because "
                + "System.IO cannot portably distinguish regular POSIX files from "
                + "sockets and FIFOs.");
        }

        return new SystemReadOnlyFileSystemProbe();
    }

    private readonly record struct AttributeObservation(
        FileAttributes Attributes,
        DestinationPathFact? Fact);
}

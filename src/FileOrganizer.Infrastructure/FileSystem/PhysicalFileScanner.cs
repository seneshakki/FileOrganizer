using System.Security;
using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>Scans the host file system without modifying it.</summary>
/// <remarks>
/// Traversal is deliberately manual and non-recursive at the System.IO layer.
/// That lets every inaccessible or disappearing directory become a diagnostic
/// instead of being silently suppressed by recursive enumeration. Reparse
/// points are reported and never followed.
/// </remarks>
public sealed class PhysicalFileScanner : IFileScanner
{
    private const FileAttributes UnsupportedAttributes = FileAttributes.Device;

    private readonly PathPolicy _pathPolicy;
    private readonly IReadOnlyFileSystemProbe _probe;

    /// <summary>Creates a physical scanner using explicit lexical path semantics.</summary>
    public PhysicalFileScanner(PathPolicy pathPolicy)
        : this(pathPolicy, new SystemReadOnlyFileSystemProbe())
    {
    }

    internal PhysicalFileScanner(
        PathPolicy pathPolicy,
        IReadOnlyFileSystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(probe);

        PathSyntax hostSyntax = OperatingSystem.IsWindows()
            ? PathSyntax.Windows
            : PathSyntax.Posix;

        if (pathPolicy.Semantics.Syntax != hostSyntax)
        {
            throw new ArgumentException(
                "The physical scanner's path syntax must match the host operating system.",
                nameof(pathPolicy));
        }

        _pathPolicy = pathPolicy;
        _probe = probe;
    }

    /// <inheritdoc />
    public FileScanResult Scan(
        CanonicalPath sourceRoot,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);

        if (sourceRoot.Semantics != _pathPolicy.Semantics)
        {
            throw new ArgumentException(
                "The source root was canonicalized under a different path policy.",
                nameof(sourceRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();

        List<FileEntry> files = [];
        List<ScanDiagnostic> diagnostics = [];

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = _probe.GetAttributes(sourceRoot.Value);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (FileNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(new ScanDiagnostic(ScanDiagnosticKind.RootNotFound, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }
        catch (DirectoryNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(new ScanDiagnostic(ScanDiagnosticKind.RootNotFound, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }
        catch (UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(new ScanDiagnostic(ScanDiagnosticKind.AccessDenied, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }
        catch (SecurityException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(new ScanDiagnostic(ScanDiagnosticKind.AccessDenied, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(new ScanDiagnostic(ScanDiagnosticKind.MetadataReadFailed, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }
        catch (NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(
                new ScanDiagnostic(ScanDiagnosticKind.UnsupportedEntrySkipped, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            diagnostics.Add(
                new ScanDiagnostic(ScanDiagnosticKind.ReparsePointSkipped, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            diagnostics.Add(
                new ScanDiagnostic(ScanDiagnosticKind.RootIsNotDirectory, sourceRoot.Value));
            return Build(sourceRoot, files, diagnostics);
        }

        Stack<CanonicalPath> pendingDirectories = new();
        pendingDirectories.Push(sourceRoot);

        while (pendingDirectories.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CanonicalPath directory = pendingDirectories.Pop();
            List<string> children = ReadDirectoryChildren(
                directory,
                diagnostics,
                cancellationToken);

            List<CanonicalPath>? subdirectories = recursive ? [] : null;

            foreach (string rawChildPath in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PathResolution resolution = _pathPolicy.CanonicalizeAbsolute(rawChildPath);
                if (!resolution.Succeeded)
                {
                    diagnostics.Add(
                        new ScanDiagnostic(ScanDiagnosticKind.InvalidEntryPath, rawChildPath));
                    continue;
                }

                CanonicalPath child = resolution.Path!;
                if (_pathPolicy.Relate(sourceRoot, child) != PathRelationship.Descendant)
                {
                    diagnostics.Add(
                        new ScanDiagnostic(ScanDiagnosticKind.EntryOutsideRoot, child.Value));
                    continue;
                }

                if (!TryReadAttributes(child, diagnostics, cancellationToken, out FileAttributes attributes))
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(
                        new ScanDiagnostic(ScanDiagnosticKind.ReparsePointSkipped, child.Value));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    subdirectories?.Add(child);
                    continue;
                }

                if ((attributes & UnsupportedAttributes) != 0)
                {
                    diagnostics.Add(
                        new ScanDiagnostic(
                            ScanDiagnosticKind.UnsupportedEntrySkipped,
                            child.Value));
                    continue;
                }

                TryReadFile(child, files, diagnostics, cancellationToken);
            }

            if (subdirectories is null)
            {
                continue;
            }

            subdirectories.Sort(_pathPolicy.DeterministicComparer);
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = subdirectories.Count - 1; index >= 0; index--)
            {
                pendingDirectories.Push(subdirectories[index]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Build(sourceRoot, files, diagnostics);
    }

    private List<string> ReadDirectoryChildren(
        CanonicalPath directory,
        List<ScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<string> children = [];

        try
        {
            using IEnumerator<string> enumerator =
                _probe.EnumerateEntries(directory.Value).GetEnumerator();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext = enumerator.MoveNext();
                cancellationToken.ThrowIfCancellationRequested();

                if (!hasNext)
                {
                    break;
                }

                children.Add(enumerator.Current);
            }
        }
        catch (FileNotFoundException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.EntryDisappeared,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.EntryDisappeared,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.AccessDenied,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (SecurityException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.AccessDenied,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (IOException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.DirectoryEnumerationFailed,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (NotSupportedException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.DirectoryEnumerationFailed,
                directory.Value,
                diagnostics,
                cancellationToken);
        }

        children.Sort(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        return children;
    }

    private bool TryReadAttributes(
        CanonicalPath path,
        List<ScanDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        out FileAttributes attributes)
    {
        attributes = default;

        try
        {
            attributes = _probe.GetAttributes(path.Value);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (FileNotFoundException)
        {
            return AttributeFailure(ScanDiagnosticKind.EntryDisappeared);
        }
        catch (DirectoryNotFoundException)
        {
            return AttributeFailure(ScanDiagnosticKind.EntryDisappeared);
        }
        catch (UnauthorizedAccessException)
        {
            return AttributeFailure(ScanDiagnosticKind.AccessDenied);
        }
        catch (SecurityException)
        {
            return AttributeFailure(ScanDiagnosticKind.AccessDenied);
        }
        catch (IOException)
        {
            return AttributeFailure(ScanDiagnosticKind.MetadataReadFailed);
        }
        catch (NotSupportedException)
        {
            return AttributeFailure(ScanDiagnosticKind.UnsupportedEntrySkipped);
        }

        bool AttributeFailure(ScanDiagnosticKind kind)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(new ScanDiagnostic(kind, path.Value));
            return false;
        }
    }

    private void TryReadFile(
        CanonicalPath path,
        List<FileEntry> files,
        List<ScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            ReadOnlyFileMetadata metadata = _probe.ReadFileMetadata(path.Value);
            cancellationToken.ThrowIfCancellationRequested();

            files.Add(
                new FileEntry(
                    path,
                    metadata.Length,
                    metadata.CreatedUtc,
                    metadata.ModifiedUtc));
        }
        catch (FileNotFoundException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.EntryDisappeared,
                path.Value,
                diagnostics,
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.EntryDisappeared,
                path.Value,
                diagnostics,
                cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.AccessDenied,
                path.Value,
                diagnostics,
                cancellationToken);
        }
        catch (SecurityException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.AccessDenied,
                path.Value,
                diagnostics,
                cancellationToken);
        }
        catch (IOException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.MetadataReadFailed,
                path.Value,
                diagnostics,
                cancellationToken);
        }
        catch (NotSupportedException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.UnsupportedEntrySkipped,
                path.Value,
                diagnostics,
                cancellationToken);
        }
    }

    private static void AddAfterCancellationCheck(
        ScanDiagnosticKind kind,
        string path,
        List<ScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics.Add(new ScanDiagnostic(kind, path));
    }

    private static FileScanResult Build(
        CanonicalPath sourceRoot,
        List<FileEntry> files,
        List<ScanDiagnostic> diagnostics) =>
        new(sourceRoot, files, diagnostics);
}

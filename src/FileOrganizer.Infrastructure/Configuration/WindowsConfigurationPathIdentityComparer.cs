using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Physical;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Configuration;

/// <summary>Compares existing Windows configuration files through FILE_ID_INFO identity.</summary>
internal sealed class WindowsConfigurationPathIdentityComparer
    : IConfigurationPathIdentityComparer
{
    private readonly WindowsPhysicalIdentityAdapter _identityAdapter;

    internal WindowsConfigurationPathIdentityComparer()
        : this(new WindowsPhysicalIdentityAdapter())
    {
    }

    internal WindowsConfigurationPathIdentityComparer(
        WindowsPhysicalIdentityAdapter identityAdapter)
    {
        ArgumentNullException.ThrowIfNull(identityAdapter);
        _identityAdapter = identityAdapter;
    }

    public ConfigurationPathIdentityComparison CompareExisting(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AreLexicallyEquivalent(firstPath, secondPath))
        {
            return ConfigurationPathIdentityComparison.SameEntry;
        }

        PhysicalEntryObservation second = _identityAdapter.ObserveAbsolutePath(
            secondPath,
            cancellationToken);
        if (second.Outcome != PhysicalEntryObservationOutcome.Success
            || second.Identity!.EntryKind != PhysicalEntryKind.RegularFile)
        {
            return ConfigurationPathIdentityComparison.Unverifiable;
        }

        PhysicalEntryObservation first = _identityAdapter.ObserveAbsolutePath(
            firstPath,
            cancellationToken);
        if (first.Outcome == PhysicalEntryObservationOutcome.Missing)
        {
            return ConfigurationPathIdentityComparison.EntryMissing;
        }

        if (first.Outcome != PhysicalEntryObservationOutcome.Success
            || first.Identity!.EntryKind != PhysicalEntryKind.RegularFile)
        {
            return ConfigurationPathIdentityComparison.Unverifiable;
        }

        return first.Identity.Equals(second.Identity)
            ? ConfigurationPathIdentityComparison.SameEntry
            : ConfigurationPathIdentityComparison.DifferentEntries;
    }

    private static bool AreLexicallyEquivalent(string firstPath, string secondPath)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(secondPath)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }
}

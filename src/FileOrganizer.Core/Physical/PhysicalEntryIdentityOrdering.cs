namespace FileOrganizer.Core.Physical;

/// <summary>
/// Provides a total, culture-independent ordering for optional physical identities.
/// </summary>
internal static class PhysicalEntryIdentityOrdering
{
    public static int Compare(PhysicalEntryIdentity? left, PhysicalEntryIdentity? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int scheme = StringComparer.Ordinal.Compare(left.Scheme, right.Scheme);
        if (scheme != 0)
        {
            return scheme;
        }

        int version = left.SchemeVersion.CompareTo(right.SchemeVersion);
        if (version != 0)
        {
            return version;
        }

        int volume = StringComparer.Ordinal.Compare(left.VolumeIdentity, right.VolumeIdentity);
        if (volume != 0)
        {
            return volume;
        }

        int identifier = StringComparer.Ordinal.Compare(
            left.EntryIdentifier,
            right.EntryIdentifier);
        return identifier != 0
            ? identifier
            : left.EntryKind.CompareTo(right.EntryKind);
    }
}

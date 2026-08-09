namespace FileOrganizer.Application.Execution;

/// <summary>
/// An opaque, platform-issued identity used only to compare two observations
/// of the same physical entry. It does not imply a cross-platform identity.
/// </summary>
public sealed class EntryIdentity : IEquatable<EntryIdentity>
{
    public EntryIdentity(string scheme, string volume, string value)
    {
        Scheme = RequireValue(scheme, nameof(scheme));
        Volume = RequireValue(volume, nameof(volume));
        Value = RequireValue(value, nameof(value));
    }

    public string Scheme { get; }

    public string Volume { get; }

    public string Value { get; }

    public bool Equals(EntryIdentity? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(Scheme, other.Scheme)
        && StringComparer.Ordinal.Equals(Volume, other.Volume)
        && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => Equals(obj as EntryIdentity);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Scheme),
        StringComparer.Ordinal.GetHashCode(Volume),
        StringComparer.Ordinal.GetHashCode(Value));

    private static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

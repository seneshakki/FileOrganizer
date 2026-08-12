using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Tests.Execution;

public sealed class PlanFingerprintTests
{
    [Fact]
    public void PreservesCanonicalLowercaseTextForValueEquality()
    {
        string lowercase = new('a', PlanFingerprint.HexLength);

        PlanFingerprint fingerprint = new(lowercase);

        Assert.Equal(lowercase, fingerprint.Value);
        Assert.Equal(fingerprint.Value, fingerprint.ToString());
        Assert.Equal(new PlanFingerprint(fingerprint.Value), fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void RejectsValuesThatAreNotExactlyOneSha256HexDigest(string value)
    {
        Assert.Throws<ArgumentException>(() => new PlanFingerprint(value));
    }
}

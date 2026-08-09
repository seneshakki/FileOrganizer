using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Tests.Execution;

public sealed class PlanFingerprintTests
{
    [Fact]
    public void NormalizesHexadecimalTextForValueEquality()
    {
        string uppercase = new('A', PlanFingerprint.HexLength);

        PlanFingerprint fingerprint = new(uppercase);

        Assert.Equal(new string('a', PlanFingerprint.HexLength), fingerprint.Value);
        Assert.Equal(fingerprint.Value, fingerprint.ToString());
        Assert.Equal(new PlanFingerprint(fingerprint.Value), fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void RejectsValuesThatAreNotExactlyOneSha256HexDigest(string value)
    {
        Assert.Throws<ArgumentException>(() => new PlanFingerprint(value));
    }
}

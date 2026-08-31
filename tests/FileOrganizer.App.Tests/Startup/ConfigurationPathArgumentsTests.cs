using FileOrganizer.App.Startup;

namespace FileOrganizer.App.Tests.Startup;

public sealed class ConfigurationPathArgumentsTests
{
    [Fact]
    public void ExactConfigArgumentAcceptsAndNormalizesAbsoluteFilePath()
    {
        string path = Path.Combine(Path.GetTempPath(), "folder", "..", "config.json");

        ConfigurationPathArguments result = ConfigurationPathArguments.Parse(["--config", path]);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(path), result.ConfigurationPath);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void NoArgumentsIsAValidFirstRunWithoutASelectedPath()
    {
        ConfigurationPathArguments result = ConfigurationPathArguments.Parse([]);

        Assert.True(result.IsValid);
        Assert.False(result.HasConfigurationPath);
        Assert.Null(result.ConfigurationPath);
        Assert.Null(result.Problem);
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("config.json")]
    [InlineData("--unknown", "C:\\config.json")]
    [InlineData("--config", "config.json")]
    [InlineData("--config", "C:\\config.json", "extra")]
    public void AnythingOtherThanOneAbsoluteConfigPathIsRejected(params string[] arguments)
    {
        ConfigurationPathArguments result = ConfigurationPathArguments.Parse(arguments);

        Assert.False(result.IsValid);
        Assert.False(result.HasConfigurationPath);
        Assert.Null(result.ConfigurationPath);
        Assert.NotEmpty(result.Problem!);
    }
}

using System.Text;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Infrastructure.Configuration;

namespace FileOrganizer.Infrastructure.Tests.Configuration;

public sealed class FileConfigurationTextStoreTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripsUnicodeAsUtf8WithoutABom()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "ayarlar", "düzenleyici.json");
        const string content = "{\n  \"name\": \"İndirilenler 📁\"\n}";
        FileConfigurationTextStore store = new();

        ConfigurationTextWriteResult write = await store.WriteAsync(
            path,
            content,
            TestContext.Current.CancellationToken);
        ConfigurationTextReadResult read = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, write.Status);
        Assert.Empty(write.Problems);
        Assert.Equal(ConfigurationTextReadStatus.Found, read.Status);
        Assert.Equal(content, read.Content);
        Assert.Empty(read.Problems);
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public async Task ASecondSaveAtomicallyReplacesThePreviousDocument()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        FileConfigurationTextStore store = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.WriteAsync(path, "{\"version\":1}", token);

        ConfigurationTextWriteResult result = await store.WriteAsync(
            path,
            "{\"version\":2}",
            token);

        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, result.Status);
        Assert.Equal("{\"version\":2}", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task FailedReplacementPreservesThePreviousValidDocument()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        CancellationToken token = TestContext.Current.CancellationToken;
        FileConfigurationTextStore initialStore = new();
        await initialStore.WriteAsync(path, "{\"version\":1}", token);
        FileConfigurationTextStore failingStore = new(new FailingPublisher());

        ConfigurationTextWriteResult result = await failingStore.WriteAsync(
            path,
            "{\"version\":2}",
            token);

        Assert.Equal(ConfigurationTextWriteStatus.Failed, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationFailure,
            Assert.Single(result.Problems).Kind);
        Assert.Equal("{\"version\":1}", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task FailedInitialPublicationLeavesNoTargetOrTemporaryFile()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        FileConfigurationTextStore store = new(new FailingPublisher());

        ConfigurationTextWriteResult result = await store.WriteAsync(
            path,
            "{}",
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Failed, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationFailure,
            Assert.Single(result.Problems).Kind);
        Assert.False(File.Exists(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task InvalidUtf8ReturnsAStructuredReadFailure()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        await File.WriteAllBytesAsync(
            path,
            [0x7b, 0xff, 0x7d],
            TestContext.Current.CancellationToken);
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult result = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.Failed, result.Status);
        Assert.Null(result.Content);
        Assert.Equal(
            ConfigurationStorageProblemKind.ReadFailure,
            Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public async Task Utf8PreambleIsAcceptedButNotReturnedAsContent()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        byte[] content = "{}"u8.ToArray();
        await File.WriteAllBytesAsync(
            path,
            [.. Encoding.UTF8.Preamble, .. content],
            TestContext.Current.CancellationToken);
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult result = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.Found, result.Status);
        Assert.Equal("{}", result.Content);
    }

    [Fact]
    public async Task RelativeAndDirectoryPathsAreRejectedStructurally()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult relativeRead = await store.ReadAsync(
            "configuration.json",
            TestContext.Current.CancellationToken);
        ConfigurationTextWriteResult directoryWrite = await store.WriteAsync(
            temp.FullPath,
            "{}",
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.Failed, relativeRead.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.InvalidPath,
            Assert.Single(relativeRead.Problems).Kind);
        Assert.Equal(ConfigurationTextWriteStatus.Failed, directoryWrite.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.InvalidPath,
            Assert.Single(directoryWrite.Problems).Kind);
    }

    [Fact]
    public async Task MissingDocumentDoesNotCreateItsParentDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string parent = Path.Combine(temp.FullPath, "not-created");
        string path = Path.Combine(parent, "configuration.json");
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult result = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.NotFound, result.Status);
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public async Task SaveCreatesOnlyItsConfigurationParent()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string parent = Path.Combine(temp.FullPath, "created", "for-config");
        string path = Path.Combine(parent, "configuration.json");
        FileConfigurationTextStore store = new();

        ConfigurationTextWriteResult result = await store.WriteAsync(
            path,
            "{}",
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, result.Status);
        Assert.Equal([path], Directory.GetFiles(parent));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task PreCanceledOperationsDoNotReadOrWrite()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        File.WriteAllText(path, "original");
        FileConfigurationTextStore store = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadAsync(path, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.WriteAsync(path, "changed", cancellation.Token).AsTask());

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    private static string[] TemporaryFiles(string destinationPath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(destinationPath)!,
            ".fileorganizer-config-*.tmp",
            SearchOption.TopDirectoryOnly);

    private sealed class FailingPublisher : IConfigurationFilePublisher
    {
        public void PublishNew(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected initial publication failure.");

        public void ReplaceExisting(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected replacement failure.");
    }
}

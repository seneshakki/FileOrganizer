namespace FileOrganizer.Infrastructure.Configuration;

internal interface IConfigurationPublicationGate
{
    TimeSpan AcquisitionTimeout { get; }

    ValueTask<IConfigurationPublicationLease> AcquireAsync(
        string canonicalPathKey,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

internal interface IConfigurationPublicationLease : IAsyncDisposable
{
    bool PreviousOwnerAbandoned { get; }
}

namespace FileOrganizer.Infrastructure.Configuration;

internal sealed class ConfigurationPublicationGateException : Exception
{
    internal ConfigurationPublicationGateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

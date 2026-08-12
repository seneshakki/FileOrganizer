using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Tests.Journaling;

internal static class TestExecutionRuns
{
    internal static ExecutionRunId Id { get; } =
        new("0123456789abcdef0123456789abcdef");
}

using FileOrganizer.Application.Orchestration;
using FileOrganizer.Infrastructure.FileSystem;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.Execution;

/// <summary>
/// Composes the Windows journaled move pipeline without exposing native or
/// persistence details to Application.
/// </summary>
public static class WindowsExecutionPipeline
{
    /// <summary>
    /// Creates an executor backed by the durable journal rooted at
    /// <paramref name="journalRoot"/>.
    /// </summary>
    /// <remarks>
    /// Construction may initialize the dedicated journal directory. User-file
    /// mutation remains behind <see cref="OrganizationPlanExecutor.ExecuteAsync"/>.
    /// </remarks>
    public static OrganizationPlanExecutor Create(
        string journalRoot,
        TimeProvider? timeProvider = null)
    {
        var physical = new WindowsPhysicalMoveAdapter(journalRoot);
        return new OrganizationPlanExecutor(
            physical,
            physical,
            physical,
            new FileExecutionJournalStore(journalRoot),
            new WindowsGlobalExecutionLeaseProvider(),
            timeProvider);
    }
}

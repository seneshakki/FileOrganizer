using FileOrganizer.Application.Journaling;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>Stable wire tokens independent of CLR enum names and ordinals.</summary>
internal static class JournalWireTokens
{
    internal static string ToToken(ExecutionJournalRecordKind value) => value switch
    {
        ExecutionJournalRecordKind.ExecutionStarted => "execution-started",
        ExecutionJournalRecordKind.MoveIntent => "move-intent",
        ExecutionJournalRecordKind.MoveApplied => "move-applied",
        ExecutionJournalRecordKind.MoveNotApplied => "move-not-applied",
        ExecutionJournalRecordKind.MoveConflict => "move-conflict",
        ExecutionJournalRecordKind.MoveAmbiguous => "move-ambiguous",
        ExecutionJournalRecordKind.MoveManualRecovery => "move-manual-recovery",
        ExecutionJournalRecordKind.ExecutionStopped => "execution-stopped",
        ExecutionJournalRecordKind.ExecutionInterrupted => "execution-interrupted",
        ExecutionJournalRecordKind.ExecutionResumed => "execution-resumed",
        ExecutionJournalRecordKind.ExecutionCompleted => "execution-completed",
        _ => throw Unsupported(value),
    };

    internal static ExecutionJournalRecordKind ParseRecordKind(string token) => token switch
    {
        "execution-started" => ExecutionJournalRecordKind.ExecutionStarted,
        "move-intent" => ExecutionJournalRecordKind.MoveIntent,
        "move-applied" => ExecutionJournalRecordKind.MoveApplied,
        "move-not-applied" => ExecutionJournalRecordKind.MoveNotApplied,
        "move-conflict" => ExecutionJournalRecordKind.MoveConflict,
        "move-ambiguous" => ExecutionJournalRecordKind.MoveAmbiguous,
        "move-manual-recovery" => ExecutionJournalRecordKind.MoveManualRecovery,
        "execution-stopped" => ExecutionJournalRecordKind.ExecutionStopped,
        "execution-interrupted" => ExecutionJournalRecordKind.ExecutionInterrupted,
        "execution-resumed" => ExecutionJournalRecordKind.ExecutionResumed,
        "execution-completed" => ExecutionJournalRecordKind.ExecutionCompleted,
        _ => throw Unsupported(token),
    };

    internal static string ToToken(JournalRecordReasonKind value) => value switch
    {
        JournalRecordReasonKind.None => "none",
        JournalRecordReasonKind.CancellationRequested => "cancellation-requested",
        JournalRecordReasonKind.ExecutionFault => "execution-fault",
        JournalRecordReasonKind.SourceRevalidationFailed => "source-revalidation-failed",
        JournalRecordReasonKind.DestinationRevalidationFailed => "destination-revalidation-failed",
        JournalRecordReasonKind.MoveDidNotApply => "move-did-not-apply",
        JournalRecordReasonKind.MoveOutcomeUnknown => "move-outcome-unknown",
        JournalRecordReasonKind.DestinationConflict => "destination-conflict",
        JournalRecordReasonKind.RecoveryConfirmedApplied => "recovery-confirmed-applied",
        JournalRecordReasonKind.RecoveryConfirmedNotApplied => "recovery-confirmed-not-applied",
        JournalRecordReasonKind.RecoverySourceIdentityChanged => "recovery-source-identity-changed",
        JournalRecordReasonKind.RecoveryTargetIdentityChanged => "recovery-target-identity-changed",
        JournalRecordReasonKind.RecoveryBothPathsContainExpectedEntry =>
            "recovery-both-paths-contain-expected-entry",
        JournalRecordReasonKind.RecoveryBothPathsMissing => "recovery-both-paths-missing",
        JournalRecordReasonKind.RecoveryInspectionFailed => "recovery-inspection-failed",
        _ => throw Unsupported(value),
    };

    internal static JournalRecordReasonKind ParseReason(string token) => token switch
    {
        "none" => JournalRecordReasonKind.None,
        "cancellation-requested" => JournalRecordReasonKind.CancellationRequested,
        "execution-fault" => JournalRecordReasonKind.ExecutionFault,
        "source-revalidation-failed" => JournalRecordReasonKind.SourceRevalidationFailed,
        "destination-revalidation-failed" => JournalRecordReasonKind.DestinationRevalidationFailed,
        "move-did-not-apply" => JournalRecordReasonKind.MoveDidNotApply,
        "move-outcome-unknown" => JournalRecordReasonKind.MoveOutcomeUnknown,
        "destination-conflict" => JournalRecordReasonKind.DestinationConflict,
        "recovery-confirmed-applied" => JournalRecordReasonKind.RecoveryConfirmedApplied,
        "recovery-confirmed-not-applied" => JournalRecordReasonKind.RecoveryConfirmedNotApplied,
        "recovery-source-identity-changed" => JournalRecordReasonKind.RecoverySourceIdentityChanged,
        "recovery-target-identity-changed" => JournalRecordReasonKind.RecoveryTargetIdentityChanged,
        "recovery-both-paths-contain-expected-entry" =>
            JournalRecordReasonKind.RecoveryBothPathsContainExpectedEntry,
        "recovery-both-paths-missing" => JournalRecordReasonKind.RecoveryBothPathsMissing,
        "recovery-inspection-failed" => JournalRecordReasonKind.RecoveryInspectionFailed,
        _ => throw Unsupported(token),
    };

    internal static string ToToken(PhysicalEntryKind value) => value switch
    {
        PhysicalEntryKind.RegularFile => "regular-file",
        PhysicalEntryKind.Directory => "directory",
        PhysicalEntryKind.ReparsePoint => "reparse-point",
        PhysicalEntryKind.Unsupported => "unsupported",
        _ => throw Unsupported(value),
    };

    internal static PhysicalEntryKind ParsePhysicalKind(string token) => token switch
    {
        "regular-file" => PhysicalEntryKind.RegularFile,
        "directory" => PhysicalEntryKind.Directory,
        "reparse-point" => PhysicalEntryKind.ReparsePoint,
        "unsupported" => PhysicalEntryKind.Unsupported,
        _ => throw Unsupported(token),
    };

    internal static string ToToken(OrganizationActionKind value) => value switch
    {
        OrganizationActionKind.MoveToDirectory => "move-to-directory",
        OrganizationActionKind.CopyToDirectory => "copy-to-directory",
        OrganizationActionKind.RenameToLiteralFileName => "rename-to-literal-file-name",
        _ => throw Unsupported(value),
    };

    internal static OrganizationActionKind ParseActionKind(string token) => token switch
    {
        "move-to-directory" => OrganizationActionKind.MoveToDirectory,
        "copy-to-directory" => OrganizationActionKind.CopyToDirectory,
        "rename-to-literal-file-name" => OrganizationActionKind.RenameToLiteralFileName,
        _ => throw Unsupported(token),
    };

    internal static string ToToken(PathSyntax value) => value switch
    {
        PathSyntax.Windows => "windows",
        PathSyntax.Posix => "posix",
        _ => throw Unsupported(value),
    };

    internal static PathSyntax ParsePathSyntax(string token) => token switch
    {
        "windows" => PathSyntax.Windows,
        "posix" => PathSyntax.Posix,
        _ => throw Unsupported(token),
    };

    internal static string ToToken(PathCaseSensitivity value) => value switch
    {
        PathCaseSensitivity.Sensitive => "sensitive",
        PathCaseSensitivity.Insensitive => "insensitive",
        _ => throw Unsupported(value),
    };

    internal static PathCaseSensitivity ParseCaseSensitivity(string token) => token switch
    {
        "sensitive" => PathCaseSensitivity.Sensitive,
        "insensitive" => PathCaseSensitivity.Insensitive,
        _ => throw Unsupported(token),
    };

    internal static string ToToken(PathUnicodeNormalization value) => value switch
    {
        PathUnicodeNormalization.None => "none",
        PathUnicodeNormalization.FormC => "form-c",
        _ => throw Unsupported(value),
    };

    internal static PathUnicodeNormalization ParseUnicodeNormalization(string token) => token switch
    {
        "none" => PathUnicodeNormalization.None,
        "form-c" => PathUnicodeNormalization.FormC,
        _ => throw Unsupported(token),
    };

    private static ArgumentOutOfRangeException Unsupported<T>(T value) =>
        new(nameof(value), value, "The value has no stable durable-journal token.");
}

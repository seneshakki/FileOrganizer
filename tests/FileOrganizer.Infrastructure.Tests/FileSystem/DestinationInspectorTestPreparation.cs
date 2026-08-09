using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

internal static class DestinationInspectorTestPreparation
{
    private static readonly DateTimeOffset Instant =
        new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);

    public static OrganizationPlanPreparation Create(
        PathPolicy policy,
        string sourceRoot,
        string destinationDirectory,
        string fileName = "report.txt")
    {
        CanonicalPath root = Canonical(policy, sourceRoot);
        CanonicalPath source = Assert.IsType<CanonicalPath>(
            policy.AppendFileName(root, fileName).Path);
        FileScanResult scan = new(
            root,
            [new FileEntry(source, 10, Instant, Instant)],
            []);
        Rule matching = new(
            "matching",
            new NameCondition(
                NameTarget.FullName,
                TextOperator.Equals,
                fileName,
                CaseSensitivity.Sensitive));
        OrganizationRuleSet rules = new(
        [
            new OrganizationRule(
                matching,
                new MoveToDirectoryAction(destinationDirectory)),
        ]);

        return new OrganizationPlanner(new FixedTimeProvider(Instant)).Prepare(
            scan,
            rules,
            policy);
    }

    public static CanonicalPath Canonical(PathPolicy policy, string path) =>
        Assert.IsType<CanonicalPath>(policy.CanonicalizeAbsolute(path).Path);

    public static PathPolicy HostPathPolicy() => new(
        OperatingSystem.IsWindows() ? PathSyntax.Windows : PathSyntax.Posix,
        OperatingSystem.IsWindows()
            ? PathCaseSensitivity.Insensitive
            : PathCaseSensitivity.Sensitive,
        PathUnicodeNormalization.None);

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}

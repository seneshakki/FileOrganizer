using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Core.Actions;

namespace FileOrganizer.Application.Tests.Journaling;

internal static class JournalTestManifest
{
    internal static ExecutionManifest Create(
        int operationCount = 1,
        string pathToken = "")
    {
        ManifestTestPlan.TestOperation[] operations = Enumerable.Range(0, operationCount)
            .Select(
                index => new ManifestTestPlan.TestOperation(
                    $@"C:\Inbox\source{pathToken}{index}.txt",
                    new MoveToDirectoryAction(@"C:\Sorted"),
                    $"move {index}"))
            .ToArray();

        ExecutionManifestCompilation compilation = ExecutionManifestCompiler.Compile(
            ManifestTestPlan.Create(operations));
        return Assert.IsType<ExecutionManifest>(compilation.Manifest);
    }
}

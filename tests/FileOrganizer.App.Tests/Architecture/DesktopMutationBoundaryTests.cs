using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace FileOrganizer.App.Tests.Architecture;

public sealed class DesktopMutationBoundaryTests
{
    private static readonly string[] ForbiddenExecutionNamespacePrefixes =
    [
        "FileOrganizer.Application.Execution.",
        "FileOrganizer.Application.Journaling.",
        "FileOrganizer.Application.Orchestration.",
        "FileOrganizer.Application.Recovery.",
        "FileOrganizer.Infrastructure.Execution.",
        "FileOrganizer.Infrastructure.Journaling.",
    ];

    private static readonly string[] ForbiddenNativeMoverTypes =
    [
        "FileOrganizer.Infrastructure.FileSystem.WindowsNoOverwriteRenameNative",
        "FileOrganizer.Infrastructure.FileSystem.WindowsPhysicalMoveAdapter",
        "FileOrganizer.Infrastructure.FileSystem.WindowsRenameBuffer",
        "FileOrganizer.Infrastructure.FileSystem.WindowsRenameResult",
    ];

    // These types expose both observations and mutations. A member must be on the
    // read-only allow-list to be used by the presentation assembly. This fails
    // closed when the BCL adds another unreviewed member name. Signatures are not
    // intentionally treated as proof that an access-mode-dependent call is safe.
    private static readonly Dictionary<string, SensitiveIoPolicy>
        SensitiveSystemIoPolicies = new Dictionary<string, SensitiveIoPolicy>(
            StringComparer.Ordinal)
        {
            ["System.IO.File"] = new(
                AllowedReadOnlyMembers:
                [
                    "Exists",
                    "GetAttributes",
                    "GetCreationTime",
                    "GetCreationTimeUtc",
                    "GetLastAccessTime",
                    "GetLastAccessTimeUtc",
                    "GetLastWriteTime",
                    "GetLastWriteTimeUtc",
                    "GetLength",
                    "GetUnixFileMode",
                    "OpenRead",
                    "OpenText",
                    "ReadAllBytes",
                    "ReadAllBytesAsync",
                    "ReadAllLines",
                    "ReadAllLinesAsync",
                    "ReadAllText",
                    "ReadAllTextAsync",
                    "ReadLines",
                    "ReadLinesAsync",
                    "ResolveLinkTarget",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    "AppendAllBytes",
                    "AppendAllBytesAsync",
                    "AppendAllLines",
                    "AppendAllLinesAsync",
                    "AppendAllText",
                    "AppendAllTextAsync",
                    "AppendText",
                    "Copy",
                    "Create",
                    "CreateHardLink",
                    "CreateSymbolicLink",
                    "CreateText",
                    "Decrypt",
                    "Delete",
                    "Encrypt",
                    "Move",
                    "Open",
                    "OpenHandle",
                    "OpenWrite",
                    "Replace",
                    "SetAttributes",
                    "SetCreationTime",
                    "SetCreationTimeUtc",
                    "SetLastAccessTime",
                    "SetLastAccessTimeUtc",
                    "SetLastWriteTime",
                    "SetLastWriteTimeUtc",
                    "SetUnixFileMode",
                    "WriteAllBytes",
                    "WriteAllBytesAsync",
                    "WriteAllLines",
                    "WriteAllLinesAsync",
                    "WriteAllText",
                    "WriteAllTextAsync",
                ]),
            ["System.IO.Directory"] = new(
                AllowedReadOnlyMembers:
                [
                    "EnumerateDirectories",
                    "EnumerateFiles",
                    "EnumerateFileSystemEntries",
                    "Exists",
                    "GetCreationTime",
                    "GetCreationTimeUtc",
                    "GetCurrentDirectory",
                    "GetDirectories",
                    "GetDirectoryRoot",
                    "GetFiles",
                    "GetFileSystemEntries",
                    "GetLastAccessTime",
                    "GetLastAccessTimeUtc",
                    "GetLastWriteTime",
                    "GetLastWriteTimeUtc",
                    "GetLogicalDrives",
                    "GetParent",
                    "ResolveLinkTarget",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    "CreateDirectory",
                    "CreateSymbolicLink",
                    "CreateTempSubdirectory",
                    "Delete",
                    "Move",
                    "SetCreationTime",
                    "SetCreationTimeUtc",
                    "SetCurrentDirectory",
                    "SetLastAccessTime",
                    "SetLastAccessTimeUtc",
                    "SetLastWriteTime",
                    "SetLastWriteTimeUtc",
                ]),
            ["System.IO.FileInfo"] = new(
                AllowedReadOnlyMembers:
                [
                    ".ctor",
                    "get_Directory",
                    "get_DirectoryName",
                    "get_IsReadOnly",
                    "get_Length",
                    "get_Name",
                    "OpenRead",
                    "OpenText",
                    "ResolveLinkTarget",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    "AppendText",
                    "CopyTo",
                    "Create",
                    "CreateAsSymbolicLink",
                    "CreateText",
                    "Decrypt",
                    "Delete",
                    "Encrypt",
                    "MoveTo",
                    "Open",
                    "OpenWrite",
                    "Replace",
                    "set_IsReadOnly",
                ]),
            ["System.IO.DirectoryInfo"] = new(
                AllowedReadOnlyMembers:
                [
                    ".ctor",
                    "EnumerateDirectories",
                    "EnumerateFiles",
                    "EnumerateFileSystemInfos",
                    "GetDirectories",
                    "GetFiles",
                    "GetFileSystemInfos",
                    "get_Parent",
                    "get_Root",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    "Create",
                    "CreateAsSymbolicLink",
                    "CreateSubdirectory",
                    "Delete",
                    "MoveTo",
                ]),
            ["System.IO.FileSystemInfo"] = new(
                AllowedReadOnlyMembers:
                [
                    "get_Attributes",
                    "get_CreationTime",
                    "get_CreationTimeUtc",
                    "get_Exists",
                    "get_Extension",
                    "get_FullName",
                    "get_LastAccessTime",
                    "get_LastAccessTimeUtc",
                    "get_LastWriteTime",
                    "get_LastWriteTimeUtc",
                    "get_LinkTarget",
                    "get_Name",
                    "get_UnixFileMode",
                    "Refresh",
                    "ResolveLinkTarget",
                    "ToString",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    "CreateAsSymbolicLink",
                    "Delete",
                    "set_Attributes",
                    "set_CreationTime",
                    "set_CreationTimeUtc",
                    "set_LastAccessTime",
                    "set_LastAccessTimeUtc",
                    "set_LastWriteTime",
                    "set_LastWriteTimeUtc",
                    "set_UnixFileMode",
                ]),
            ["System.IO.FileStream"] = new(
                AllowedReadOnlyMembers:
                [
                    "get_CanRead",
                    "get_CanSeek",
                    "get_CanTimeout",
                    "get_CanWrite",
                    "get_Handle",
                    "get_IsAsync",
                    "get_Length",
                    "get_Name",
                    "get_Position",
                    "get_ReadTimeout",
                    "get_SafeFileHandle",
                    "get_WriteTimeout",
                    "Read",
                    "ReadAsync",
                    "ReadAtLeast",
                    "ReadAtLeastAsync",
                    "ReadByte",
                    "ReadExactly",
                    "ReadExactlyAsync",
                    "Seek",
                    "set_Position",
                    "set_ReadTimeout",
                    "set_WriteTimeout",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    // Constructor access flags are not visible at MemberReference
                    // level. Presentation code must use an explicit read-only API.
                    ".ctor",
                    "BeginWrite",
                    "EndWrite",
                    "Flush",
                    "FlushAsync",
                    "Lock",
                    "SetLength",
                    "Unlock",
                    "Write",
                    "WriteAsync",
                    "WriteByte",
                ]),
            ["System.IO.StreamWriter"] = new(
                AllowedReadOnlyMembers:
                [
                    "get_AutoFlush",
                    "get_BaseStream",
                    "get_Encoding",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    // Includes the path-taking constructors. MemberReference
                    // metadata cannot prove that a different overload is harmless,
                    // and StreamWriter has no read-only presentation use.
                    ".ctor",
                    "Flush",
                    "FlushAsync",
                    "set_AutoFlush",
                    "Write",
                    "WriteAsync",
                    "WriteLine",
                    "WriteLineAsync",
                ]),
            ["System.IO.RandomAccess"] = new(
                AllowedReadOnlyMembers:
                [
                    "GetLength",
                    "Read",
                    "ReadAsync",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    "FlushToDisk",
                    "SetLength",
                    "Write",
                    "WriteAsync",
                ]),
            ["System.IO.FileSystemAclExtensions"] = new(
                AllowedReadOnlyMembers: ["GetAccessControl"],
                KnownMutationOrAmbiguousMembers: ["SetAccessControl"]),
            ["Avalonia.Platform.Storage.IStorageItem"] = new(
                AllowedReadOnlyMembers:
                [
                    "get_CanBookmark",
                    "get_Name",
                    "get_Path",
                    "GetBasicPropertiesAsync",
                    "GetParentAsync",
                    "SaveBookmarkAsync",
                ],
                KnownMutationOrAmbiguousMembers: ["DeleteAsync", "MoveAsync"]),
            ["Avalonia.Platform.Storage.IStorageFile"] = new(
                AllowedReadOnlyMembers: ["OpenReadAsync"],
                KnownMutationOrAmbiguousMembers: ["OpenWriteAsync"]),
            ["Avalonia.Platform.Storage.IStorageFolder"] = new(
                AllowedReadOnlyMembers:
                [
                    "GetFileAsync",
                    "GetFolderAsync",
                    "GetItemsAsync",
                ],
                KnownMutationOrAmbiguousMembers:
                [
                    "CreateFileAsync",
                    "CreateFolderAsync",
                ]),
        };

    // Most Path operations are pure string manipulation. GetTempFileName is the
    // notable exception because it creates a file as a side effect.
    private static readonly Dictionary<string, HashSet<string>>
        KnownDirectMutationMembersByType = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal)
        {
            ["System.IO.Path"] = CreateMemberSet("GetTempFileName"),
        };

    // These are not normal presentation responsibilities. Unlike the strict
    // System.IO policies above, only invocation-capable members are denied so
    // benign diagnostics such as Process.GetCurrentProcess remain possible.
    private static readonly Dictionary<string, HashSet<string>>
        ForbiddenEscapeMembersByType = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal)
        {
            ["System.Diagnostics.Process"] = CreateMemberSet("Kill", "Start"),
            ["Microsoft.VisualBasic.Interaction"] = CreateMemberSet("Shell"),
            ["System.Runtime.InteropServices.Marshal"] = CreateMemberSet(
                "GetDelegateForFunctionPointer",
                "GetFunctionPointerForDelegate"),
        };

    // Any NativeLibrary call can turn a dynamically resolved export into an
    // unreviewed native mutation path, so this type intentionally has no allow-list.
    private static readonly HashSet<string> FullyForbiddenInvocationTypes = new(
        ["System.Runtime.InteropServices.NativeLibrary"],
        StringComparer.Ordinal);

    [Fact]
    public void ProductionAssemblyDoesNotReferenceExecutionSurfaces()
    {
        using FileStream assemblyStream = File.OpenRead(GetProductionAssemblyPath());
        using var peReader = new PEReader(assemblyStream);
        Assert.True(peReader.HasMetadata);

        MetadataReader metadata = peReader.GetMetadataReader();
        List<string> violations = [];

        foreach (TypeReferenceHandle handle in metadata.TypeReferences)
        {
            string typeName = GetTypeReferenceName(metadata, handle);
            if (IsForbiddenExecutionType(typeName))
            {
                violations.Add($"type: {typeName}");
            }
        }

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            string declaringType = GetTypeReferenceName(
                metadata,
                (TypeReferenceHandle)member.Parent);
            if (IsForbiddenExecutionType(declaringType))
            {
                violations.Add(
                    $"member: {declaringType}::{metadata.GetString(member.Name)}");
            }
        }

        AssertNoViolations(
            violations,
            "The desktop production assembly must not bind the execution, journal, "
                + "lease, recovery, or native mover surface.");
    }

    // This is a direct-reference boundary, not a whole-program taint proof. It
    // also blocks common process/PInvoke/dynamic-native escape hatches so the
    // usual direct presentation-layer escape routes are reported as violations.
    [Fact]
    public void ProductionAssemblyDoesNotBindDirectFilesystemMutationOrEscapeApis()
    {
        using FileStream assemblyStream = File.OpenRead(GetProductionAssemblyPath());
        using var peReader = new PEReader(assemblyStream);
        Assert.True(peReader.HasMetadata);

        MetadataReader metadata = peReader.GetMetadataReader();
        List<string> violations = [];

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            string declaringType = GetTypeReferenceName(
                metadata,
                (TypeReferenceHandle)member.Parent);
            string memberName = metadata.GetString(member.Name);
            string? policyReason = GetForbiddenDirectMemberReason(
                declaringType,
                memberName);
            if (policyReason is not null)
            {
                violations.Add(
                    $"member: {declaringType}::{memberName} ({policyReason})");
            }
        }

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
            {
                continue;
            }

            MethodImport import = method.GetImport();
            ModuleReference module = metadata.GetModuleReference(import.Module);
            violations.Add(
                $"pinvoke: {GetMethodDefinitionName(metadata, handle)} -> "
                    + $"{metadata.GetString(module.Name)}!{metadata.GetString(import.Name)}");
        }

        AssertNoViolations(
            violations,
            "The desktop production assembly must not bind direct filesystem mutation, "
                + "process-launch, or native-interoperability escape APIs.");
    }

    [Theory]
    [InlineData("System.IO.File", "ReadAllText", false)]
    [InlineData("System.IO.File", "WriteAllText", true)]
    [InlineData("System.IO.File", "Open", true)]
    [InlineData("System.IO.File", "FutureUnreviewedMember", true)]
    [InlineData("System.IO.Directory", "EnumerateFiles", false)]
    [InlineData("System.IO.Directory", "CreateDirectory", true)]
    [InlineData("System.IO.Path", "GetRandomFileName", false)]
    [InlineData("System.IO.Path", "GetTempFileName", true)]
    [InlineData("System.IO.FileInfo", "OpenRead", false)]
    [InlineData("System.IO.FileInfo", "MoveTo", true)]
    [InlineData("System.IO.DirectoryInfo", "GetFiles", false)]
    [InlineData("System.IO.DirectoryInfo", "Delete", true)]
    [InlineData("System.IO.FileSystemInfo", "set_Attributes", true)]
    [InlineData("System.IO.FileStream", ".ctor", true)]
    [InlineData("System.IO.StreamWriter", "get_Encoding", false)]
    [InlineData("System.IO.StreamWriter", ".ctor", true)]
    [InlineData("System.IO.RandomAccess", "Read", false)]
    [InlineData("System.IO.RandomAccess", "Write", true)]
    [InlineData("Avalonia.Platform.Storage.IStorageFile", "OpenReadAsync", false)]
    [InlineData("Avalonia.Platform.Storage.IStorageFile", "OpenWriteAsync", true)]
    [InlineData("Avalonia.Platform.Storage.IStorageItem", "DeleteAsync", true)]
    [InlineData("System.Diagnostics.Process", "GetCurrentProcess", false)]
    [InlineData("System.Diagnostics.Process", "Start", true)]
    [InlineData("Microsoft.VisualBasic.Interaction", "Shell", true)]
    [InlineData("System.Runtime.InteropServices.NativeLibrary", "Load", true)]
    [InlineData("System.Runtime.InteropServices.Marshal", "SizeOf", false)]
    [InlineData("System.Runtime.InteropServices.Marshal", "GetDelegateForFunctionPointer", true)]
    public void DirectMemberPolicyDistinguishesReadOnlyAndMutatingBindings(
        string declaringType,
        string memberName,
        bool expectedForbidden)
    {
        Assert.Equal(
            expectedForbidden,
            GetForbiddenDirectMemberReason(declaringType, memberName) is not null);
    }

    private static string GetProductionAssemblyPath()
    {
        System.Reflection.Assembly assembly = typeof(global::FileOrganizer.App.App).Assembly;
        Assert.Equal("FileOrganizer.App", assembly.GetName().Name);
        Assert.False(string.IsNullOrWhiteSpace(assembly.Location));
        return assembly.Location;
    }

    private static string GetTypeReferenceName(
        MetadataReader metadata,
        TypeReferenceHandle handle)
    {
        TypeReference reference = metadata.GetTypeReference(handle);
        string name = metadata.GetString(reference.Name);
        string typeNamespace = metadata.GetString(reference.Namespace);

        if (!string.IsNullOrEmpty(typeNamespace))
        {
            return $"{typeNamespace}.{name}";
        }

        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{GetTypeReferenceName(metadata, (TypeReferenceHandle)reference.ResolutionScope)}+{name}"
            : name;
    }

    private static bool IsForbiddenExecutionType(string typeName)
    {
        if (ForbiddenExecutionNamespacePrefixes.Any(
                prefix => typeName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return true;
        }

        return ForbiddenNativeMoverTypes.Any(
            forbiddenType => typeName.Equals(forbiddenType, StringComparison.Ordinal)
                || typeName.StartsWith($"{forbiddenType}+", StringComparison.Ordinal));
    }

    private static string? GetForbiddenDirectMemberReason(
        string declaringType,
        string memberName)
    {
        if (SensitiveSystemIoPolicies.TryGetValue(
                declaringType,
                out SensitiveIoPolicy? policy))
        {
            if (policy.KnownMutationOrAmbiguousMembers.Contains(memberName))
            {
                return "known mutating or access-mode-dependent API";
            }

            return policy.AllowedReadOnlyMembers.Contains(memberName)
                ? null
                : "unreviewed member on a filesystem-sensitive type";
        }

        if (KnownDirectMutationMembersByType.TryGetValue(
                declaringType,
                out HashSet<string>? directMutationMembers)
            && directMutationMembers.Contains(memberName))
        {
            return "known filesystem mutation API";
        }

        if (FullyForbiddenInvocationTypes.Contains(declaringType))
        {
            return "dynamic native invocation API";
        }

        return ForbiddenEscapeMembersByType.TryGetValue(
                declaringType,
                out HashSet<string>? forbiddenMembers)
            && forbiddenMembers.Contains(memberName)
                ? "process or native invocation API"
                : null;
    }

    private static string GetMethodDefinitionName(
        MetadataReader metadata,
        MethodDefinitionHandle handle)
    {
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        TypeDefinition declaringType = metadata.GetTypeDefinition(method.GetDeclaringType());
        string typeNamespace = metadata.GetString(declaringType.Namespace);
        string typeName = metadata.GetString(declaringType.Name);
        string qualifiedTypeName = string.IsNullOrEmpty(typeNamespace)
            ? typeName
            : $"{typeNamespace}.{typeName}";

        return $"{qualifiedTypeName}::{metadata.GetString(method.Name)}";
    }

    private static HashSet<string> CreateMemberSet(params string[] members) =>
        new(members, StringComparer.Ordinal);

    private static void AssertNoViolations(
        IEnumerable<string> violations,
        string boundaryDescription)
    {
        string[] distinctViolations = violations
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            distinctViolations.Length == 0,
            $"{boundaryDescription}{Environment.NewLine}{string.Join(Environment.NewLine, distinctViolations)}");
    }

    private sealed record SensitiveIoPolicy(
        IReadOnlySet<string> AllowedReadOnlyMembers,
        IReadOnlySet<string> KnownMutationOrAmbiguousMembers)
    {
        public SensitiveIoPolicy(
            IEnumerable<string> AllowedReadOnlyMembers,
            IEnumerable<string> KnownMutationOrAmbiguousMembers)
            : this(
                new HashSet<string>(AllowedReadOnlyMembers, StringComparer.Ordinal),
                new HashSet<string>(KnownMutationOrAmbiguousMembers, StringComparer.Ordinal))
        {
        }
    }
}

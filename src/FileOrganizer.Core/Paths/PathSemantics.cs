namespace FileOrganizer.Core.Paths;

/// <summary>
/// All choices that determine how paths are parsed and compared.
/// </summary>
public readonly record struct PathSemantics(
    PathSyntax Syntax,
    PathCaseSensitivity CaseSensitivity,
    PathUnicodeNormalization UnicodeNormalization);

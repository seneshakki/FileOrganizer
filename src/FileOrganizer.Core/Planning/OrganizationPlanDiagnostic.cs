namespace FileOrganizer.Core.Planning;

/// <summary>A structured diagnostic applying to the plan as a whole.</summary>
public sealed record OrganizationPlanDiagnostic
{
    public OrganizationPlanDiagnostic(
        OrganizationPlanDiagnosticKind kind,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Kind = kind;
        Description = description;
    }

    /// <summary>Gets the stable diagnostic category.</summary>
    public OrganizationPlanDiagnosticKind Kind { get; }

    /// <summary>Gets the explanation suitable for a dry-run preview.</summary>
    public string Description { get; }
}

namespace FileOrganizer.Application.Configuration;

/// <summary>A stable category explaining why configuration could not be used.</summary>
public enum ConfigurationProblemKind
{
    MalformedJson,
    UnsupportedSchemaVersion,
    MissingRequiredField,
    UnknownProperty,
    DuplicateProperty,
    InvalidValue,
    UnknownConditionDiscriminator,
    UnknownActionDiscriminator,
    DuplicateRuleId,
    InvalidRule,
    InvalidCondition,
    ImpossibleLogicalTree,
    InvalidAction,
    InvalidPath,
    UnsupportedDomainCondition,
}

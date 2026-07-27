namespace UnifyEmpi.Domain;

public readonly record struct TenantId
{
    public TenantId(string value)
    {
        RegistryIdValidator.Validate(value, nameof(value));
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct SourceSystemId
{
    public SourceSystemId(string value)
    {
        RegistryIdValidator.Validate(value, nameof(value));
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct EnterpriseId
{
    public EnterpriseId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Enterprise ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static EnterpriseId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct SourceRecordKey(SourceSystemId SourceSystem, string LocalId)
{
    public override string ToString() => $"{SourceSystem.Value}/{LocalId}";
}

public enum AdministrativeGender
{
    Unknown,
    Male,
    Female,
    Other
}

public enum NameUse
{
    Unknown,
    Usual,
    Official,
    Temp,
    Nickname,
    Anonymous,
    Old,
    Maiden
}

public enum AddressUse
{
    Unknown,
    Home,
    Work,
    Temp,
    Old,
    Billing
}

public enum ContactPointSystem
{
    Unknown,
    Phone,
    Fax,
    Email,
    Pager,
    Url,
    Sms,
    Other
}

public sealed record IdentityIdentifier(
    string System,
    string Value,
    bool IsVerified = false,
    bool IsAuthoritative = false,
    string? Use = null);

public sealed record IdentityTag(
    string? System,
    string Code,
    string? Display = null);

public sealed record PersonName(
    string? Family,
    IReadOnlyList<string> Given,
    NameUse Use = NameUse.Unknown,
    string? Prefix = null,
    string? Suffix = null);

public sealed record PostalAddress(
    IReadOnlyList<string> Lines,
    string? City,
    string? District,
    string? PostalCode,
    string? Country,
    AddressUse Use = AddressUse.Unknown);

public sealed record ContactPoint(
    ContactPointSystem System,
    string Value,
    string? Use = null);

public sealed record IdentityProfile(
    IReadOnlyList<IdentityIdentifier> Identifiers,
    IReadOnlyList<PersonName> Names,
    DateOnly? BirthDate,
    AdministrativeGender Gender,
    IReadOnlyList<PostalAddress> Addresses,
    IReadOnlyList<ContactPoint> Telecoms,
    bool IsDeceased = false)
{
    public IReadOnlyList<IdentityTag> Tags { get; init; } = [];

    public static IdentityProfile Empty { get; } = new(
        [],
        [],
        null,
        AdministrativeGender.Unknown,
        [],
        []);
}

public sealed record SourcePatientRecord(
    SourceRecordKey Key,
    string ResourceId,
    EnterpriseId EnterpriseId,
    IdentityProfile Profile,
    int SourceTrust,
    DateTimeOffset LastUpdated,
    long Version);

public sealed record CanonicalPatient(
    EnterpriseId EnterpriseId,
    IdentityProfile Profile,
    IReadOnlyList<SourceRecordKey> Sources,
    IReadOnlyList<BlockingKey> BlockingKeys,
    int SurvivorshipTrust,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdated,
    long Version,
    bool IsActive = true,
    EnterpriseId? ReplacedBy = null);

public enum LinkAssurance
{
    Level1,
    Level2,
    Level3,
    Level4
}

public sealed record PersonLink(
    SourceRecordKey Source,
    string SourceResourceId,
    LinkAssurance Assurance,
    DateTimeOffset LinkedAt,
    string Reason);

public sealed record EnterprisePerson(
    EnterpriseId EnterpriseId,
    IReadOnlyList<PersonLink> Links,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdated,
    long Version,
    bool IsActive = true,
    EnterpriseId? ReplacedBy = null);

internal static class RegistryIdValidator
{
    public static void Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 64 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "Registry identifiers must be 1-64 ASCII letters, digits, periods, underscores, or hyphens, and start with a letter or digit.",
                parameterName);
        }
    }
}

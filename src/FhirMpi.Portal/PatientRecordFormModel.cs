using System.ComponentModel.DataAnnotations;
using FhirMpi.Application.Normalisation;
using FhirMpi.Domain;

namespace FhirMpi.Portal;

public sealed class PatientRecordFormModel : IValidatableObject
{
    private const string LocalReferencePattern = "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$";

    [Required(ErrorMessage = "Enter a local patient reference.")]
    [RegularExpression(
        LocalReferencePattern,
        ErrorMessage = "Use 1–64 letters, numbers, periods, underscores or hyphens.")]
    public string LocalReference { get; set; } = string.Empty;

    public string? NhsNumber { get; set; }

    [Required(ErrorMessage = "Enter the family name.")]
    [StringLength(200, ErrorMessage = "The family name cannot exceed 200 characters.")]
    public string FamilyName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter at least one given name.")]
    [StringLength(300, ErrorMessage = "Given names cannot exceed 300 characters.")]
    public string GivenNames { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the date of birth.")]
    public DateOnly? BirthDate { get; set; }

    public AdministrativeGender Gender { get; set; } = AdministrativeGender.Unknown;

    [StringLength(300, ErrorMessage = "The address line cannot exceed 300 characters.")]
    public string? AddressLine { get; set; }

    [StringLength(150, ErrorMessage = "The town or city cannot exceed 150 characters.")]
    public string? City { get; set; }

    [StringLength(12, ErrorMessage = "The postcode cannot exceed 12 characters.")]
    public string? Postcode { get; set; }

    [StringLength(80, ErrorMessage = "The telephone number cannot exceed 80 characters.")]
    public string? Phone { get; set; }

    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(254, ErrorMessage = "The email address cannot exceed 254 characters.")]
    public string? Email { get; set; }

    public bool IsDeceased { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(NhsNumber) && !NhsNumberValidator.IsValid(NhsNumber))
        {
            yield return new ValidationResult(
                "Enter a valid 10-digit NHS number with the correct check digit.",
                [nameof(NhsNumber)]);
        }

        if (BirthDate is { } birthDate &&
            birthDate > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            yield return new ValidationResult(
                "The date of birth cannot be in the future.",
                [nameof(BirthDate)]);
        }
    }

    public IdentityProfile ToProfile(IdentityProfile? existing = null)
    {
        var identifiers = (existing?.Identifiers ?? [])
            .Where(static identifier => !string.Equals(
                identifier.System,
                NhsNumberValidator.IdentifierSystem,
                StringComparison.Ordinal))
            .ToList();
        var normalisedNhsNumber = NhsNumberValidator.Normalise(NhsNumber);
        if (normalisedNhsNumber.Length > 0)
        {
            identifiers.Add(new IdentityIdentifier(
                NhsNumberValidator.IdentifierSystem,
                normalisedNhsNumber));
        }

        var names = (existing?.Names ?? [])
            .Where(static name => name.Use is NameUse.Old or NameUse.Maiden or NameUse.Nickname)
            .Append(new PersonName(
                FamilyName.Trim(),
                SplitGivenNames(GivenNames),
                NameUse.Official))
            .ToArray();

        var addresses = (existing?.Addresses ?? [])
            .Where(static address => address.Use is AddressUse.Old or AddressUse.Work or AddressUse.Billing)
            .ToList();
        if (HasValue(AddressLine) || HasValue(City) || HasValue(Postcode))
        {
            addresses.Add(new PostalAddress(
                HasValue(AddressLine) ? [AddressLine!.Trim()] : [],
                TrimOrNull(City),
                null,
                TrimOrNull(Postcode)?.ToUpperInvariant(),
                "GB",
                AddressUse.Home));
        }

        var telecoms = (existing?.Telecoms ?? [])
            .Where(static contact =>
                contact.System is not ContactPointSystem.Phone and not ContactPointSystem.Email)
            .ToList();
        if (HasValue(Phone))
        {
            telecoms.Add(new ContactPoint(ContactPointSystem.Phone, Phone!.Trim(), "mobile"));
        }

        if (HasValue(Email))
        {
            telecoms.Add(new ContactPoint(
                ContactPointSystem.Email,
                Email!.Trim().ToLowerInvariant(),
                "home"));
        }

        return new IdentityProfile(
            identifiers,
            names,
            BirthDate,
            Gender,
            addresses,
            telecoms,
            IsDeceased);
    }

    public static PatientRecordFormModel From(SourcePatientRecord source)
    {
        var profile = source.Profile;
        var name = profile.Names.FirstOrDefault(static name => name.Use == NameUse.Official) ??
                   (profile.Names.Count > 0 ? profile.Names[0] : null);
        var address = profile.Addresses.FirstOrDefault(static address => address.Use == AddressUse.Home) ??
                      (profile.Addresses.Count > 0 ? profile.Addresses[0] : null);
        return new PatientRecordFormModel
        {
            LocalReference = source.Key.LocalId,
            NhsNumber = profile.Identifiers.FirstOrDefault(static identifier =>
                string.Equals(
                    identifier.System,
                    NhsNumberValidator.IdentifierSystem,
                    StringComparison.Ordinal))?.Value,
            FamilyName = name?.Family ?? string.Empty,
            GivenNames = name is null ? string.Empty : string.Join(' ', name.Given),
            BirthDate = profile.BirthDate,
            Gender = profile.Gender,
            AddressLine = address is null ? null : string.Join(", ", address.Lines),
            City = address?.City,
            Postcode = address?.PostalCode,
            Phone = profile.Telecoms.FirstOrDefault(static contact =>
                contact.System == ContactPointSystem.Phone)?.Value,
            Email = profile.Telecoms.FirstOrDefault(static contact =>
                contact.System == ContactPointSystem.Email)?.Value,
            IsDeceased = profile.IsDeceased
        };
    }

    private static string[] SplitGivenNames(string value) =>
        value.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string? TrimOrNull(string? value) =>
        HasValue(value) ? value!.Trim() : null;
}

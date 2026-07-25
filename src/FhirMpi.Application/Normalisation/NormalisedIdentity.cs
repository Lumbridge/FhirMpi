using FhirMpi.Domain;

namespace FhirMpi.Application.Normalisation;

public sealed record NormalisedName(
    string Family,
    IReadOnlyList<string> Given,
    string FamilyPhonetic);

public sealed record NormalisedAddress(
    string AddressTokens,
    string PostalCode,
    string PostalSector);

public sealed record NormalisedTelecom(
    ContactPointSystem System,
    string Value);

public sealed record NormalisedIdentity(
    IReadOnlyList<IdentityIdentifier> Identifiers,
    IReadOnlyList<NormalisedName> Names,
    DateOnly? BirthDate,
    AdministrativeGender Gender,
    IReadOnlyList<NormalisedAddress> Addresses,
    IReadOnlyList<NormalisedTelecom> Telecoms);

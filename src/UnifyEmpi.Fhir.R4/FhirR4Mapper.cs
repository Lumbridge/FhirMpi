using System.Globalization;
using Hl7.Fhir.Model;
using Domain = UnifyEmpi.Domain;

namespace UnifyEmpi.Fhir.R4;

public static class FhirR4Mapper
{
    public static Domain.IdentityProfile ToDomain(Patient patient)
        => ToDomain(patient, trustServerManagedExtensions: false);

    public static Domain.IdentityProfile ToTrustedDomain(Patient patient)
        => ToDomain(patient, trustServerManagedExtensions: true);

    private static Domain.IdentityProfile ToDomain(
        Patient patient,
        bool trustServerManagedExtensions)
    {
        ArgumentNullException.ThrowIfNull(patient);

        var identifiers = patient.Identifier
            .Where(static identifier =>
                !string.IsNullOrWhiteSpace(identifier.System) &&
                !string.IsNullOrWhiteSpace(identifier.Value))
            .Select(identifier => new Domain.IdentityIdentifier(
                identifier.System!,
                identifier.Value!,
                trustServerManagedExtensions &&
                GetBooleanExtension(identifier, FhirR4Constants.IdentifierVerifiedExtension),
                trustServerManagedExtensions &&
                GetBooleanExtension(identifier, FhirR4Constants.IdentifierAuthoritativeExtension),
                identifier.Use?.ToString().ToLowerInvariant()))
            .ToArray();
        var names = patient.Name
            .Select(name => new Domain.PersonName(
                name.Family,
                name.Given.OfType<string>()
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray(),
                MapNameUse(name.Use),
                Join(name.Prefix),
                Join(name.Suffix)))
            .ToArray();
        var addresses = patient.Address
            .Select(address => new Domain.PostalAddress(
                address.Line.OfType<string>()
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray(),
                address.City,
                address.District,
                address.PostalCode,
                address.Country,
                MapAddressUse(address.Use)))
            .ToArray();
        var telecoms = patient.Telecom
            .Where(static contact => !string.IsNullOrWhiteSpace(contact.Value))
            .Select(contact => new Domain.ContactPoint(
                MapContactSystem(contact.System),
                contact.Value!,
                contact.Use?.ToString().ToLowerInvariant()))
            .ToArray();
        var tags = patient.Meta?.Tag
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Code))
            .Select(static tag => new Domain.IdentityTag(
                tag.System,
                tag.Code!,
                tag.Display))
            .Distinct()
            .ToArray() ?? [];

        return new Domain.IdentityProfile(
            identifiers,
            names,
            ParseDate(patient.BirthDate),
            MapGender(patient.Gender),
            addresses,
            telecoms,
            patient.Deceased is FhirBoolean { Value: true })
        {
            Tags = tags
        };
    }

    public static Patient ToSourcePatient(
        Domain.SourcePatientRecord source,
        Domain.TenantId tenantId)
    {
        var patient = ToPatient(source.Profile);
        patient.Id = source.ResourceId;
        patient.Meta = CreateMeta(
            tenantId,
            source.Version,
            source.LastUpdated,
            FhirR4Constants.UkCorePatientProfile);
        AddIdentityTags(patient.Meta, source.Profile.Tags);
        patient.Identifier.Add(new Identifier(
            FhirR4Constants.EnterpriseIdentifierSystem,
            source.EnterpriseId.ToString())
        {
            Use = Identifier.IdentifierUse.Secondary
        });
        patient.Extension.Add(new Extension(
            FhirR4Constants.SourceSystemExtension,
            new FhirString(source.Key.SourceSystem.Value)));
        return patient;
    }

    public static Patient ToCanonicalPatient(
        Domain.CanonicalPatient canonical,
        Domain.TenantId tenantId)
    {
        var patient = ToPatient(canonical.Profile);
        patient.Id = canonical.EnterpriseId.ToString();
        patient.Active = canonical.IsActive;
        patient.Meta = CreateMeta(
            tenantId,
            canonical.Version,
            canonical.LastUpdated,
            FhirR4Constants.UkCorePatientProfile);
        AddIdentityTags(patient.Meta, canonical.Profile.Tags);
        patient.Identifier.Insert(0, new Identifier(
            FhirR4Constants.EnterpriseIdentifierSystem,
            canonical.EnterpriseId.ToString())
        {
            Use = Identifier.IdentifierUse.Official
        });
        if (canonical.ReplacedBy is { } replacedBy)
        {
            patient.Link.Add(new Patient.LinkComponent
            {
                Other = new ResourceReference($"Patient/{replacedBy}"),
                Type = Patient.LinkType.ReplacedBy
            });
        }

        return patient;
    }

    public static Person ToPerson(
        Domain.EnterprisePerson person,
        Domain.TenantId tenantId)
    {
        var resource = new Person
        {
            Id = person.EnterpriseId.ToString(),
            Active = person.IsActive,
            Meta = CreateMeta(tenantId, person.Version, person.LastUpdated)
        };
        resource.Identifier.Add(new Identifier(
            FhirR4Constants.EnterpriseIdentifierSystem,
            person.EnterpriseId.ToString()));
        foreach (var link in person.Links)
        {
            resource.Link.Add(new Person.LinkComponent
            {
                Target = new ResourceReference($"Patient/{link.SourceResourceId}"),
                Assurance = link.Assurance switch
                {
                    Domain.LinkAssurance.Level1 => Person.IdentityAssuranceLevel.Level1,
                    Domain.LinkAssurance.Level2 => Person.IdentityAssuranceLevel.Level2,
                    Domain.LinkAssurance.Level3 => Person.IdentityAssuranceLevel.Level3,
                    Domain.LinkAssurance.Level4 => Person.IdentityAssuranceLevel.Level4,
                    _ => Person.IdentityAssuranceLevel.Level1
                }
            });
        }

        if (person.ReplacedBy is { } replacedBy)
        {
            resource.Link.Add(new Person.LinkComponent
            {
                Target = new ResourceReference($"Person/{replacedBy}"),
                Assurance = Person.IdentityAssuranceLevel.Level4
            });
        }

        return resource;
    }

    public static Bundle ToMatchBundle(
        Domain.MatchResponse response,
        Domain.TenantId tenantId,
        Uri? self = null)
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Searchset,
            Total = response.Matches.Count,
            Timestamp = DateTimeOffset.UtcNow
        };
        if (self is not null)
        {
            bundle.Link.Add(new Bundle.LinkComponent
            {
                Relation = "self",
                Url = self.ToString()
            });
        }

        foreach (var match in response.Matches)
        {
            var patient = ToCanonicalPatient(match.Patient, tenantId);
            patient.Extension.Add(new Extension(
                FhirR4Constants.MatchGradeExtension,
                new Code(match.Grade.ToString().ToLowerInvariant())));
            patient.Extension.Add(new Extension(
                FhirR4Constants.MatchScoreMethodExtension,
                new Code(match.ScoreMethod)));
            patient.Extension.Add(new Extension(
                FhirR4Constants.MatchEvidenceExtension,
                new FhirString(string.Join(
                    "; ",
                    match.Evidence.Select(static item =>
                        $"{item.Field}:{item.Similarity.ToString("0.###", CultureInfo.InvariantCulture)}:{item.Comparator}:{item.ComparisonLevel}" +
                        (item.LogLikelihoodRatio.HasValue
                            ? $":llr={item.LogLikelihoodRatio.Value.ToString("0.###", CultureInfo.InvariantCulture)}"
                            : string.Empty))))));
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"Patient/{patient.Id}",
                Resource = patient,
                Search = new Bundle.SearchComponent
                {
                    Mode = Bundle.SearchEntryMode.Match,
                    Score = (decimal)match.Score
                }
            });
        }

        return bundle;
    }

    public static Bundle ToPatientSearchBundle(
        IReadOnlyList<Domain.CanonicalPatient> patients,
        Domain.TenantId tenantId,
        Uri self,
        Uri? next)
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Searchset,
            Total = patients.Count,
            Timestamp = DateTimeOffset.UtcNow
        };
        bundle.Link.Add(new Bundle.LinkComponent
        {
            Relation = "self",
            Url = self.ToString()
        });
        if (next is not null)
        {
            bundle.Link.Add(new Bundle.LinkComponent
            {
                Relation = "next",
                Url = next.ToString()
            });
        }

        foreach (var patient in patients)
        {
            var resource = ToCanonicalPatient(patient, tenantId);
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"Patient/{resource.Id}",
                Resource = resource,
                Search = new Bundle.SearchComponent
                {
                    Mode = Bundle.SearchEntryMode.Match
                }
            });
        }

        return bundle;
    }

    public static OperationOutcome CreateOperationOutcome(
        OperationOutcome.IssueType code,
        string diagnostics,
        OperationOutcome.IssueSeverity severity = OperationOutcome.IssueSeverity.Error,
        string? expression = null)
    {
        var issue = new OperationOutcome.IssueComponent
        {
            Severity = severity,
            Code = code,
            Diagnostics = diagnostics
        };
        if (expression is not null)
        {
            issue.Expression = [expression];
        }

        return new OperationOutcome
        {
            Issue = [issue]
        };
    }

    public static void AssertTenant(Resource resource, Domain.TenantId tenantId)
    {
        var tenantLabels = resource.Meta?.Security
            .Where(coding => string.Equals(
                coding.System,
                FhirR4Constants.TenantSecuritySystem,
                StringComparison.Ordinal))
            .Select(static coding => coding.Code)
            .ToArray() ?? [];
        if (tenantLabels.Length != 1 ||
            !string.Equals(tenantLabels[0], tenantId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resource tenant security label is missing or incorrect.");
        }
    }

    public static Meta CreateMeta(
        Domain.TenantId tenantId,
        long version,
        DateTimeOffset lastUpdated,
        params string[] profiles) =>
        new()
        {
            VersionId = version.ToString(CultureInfo.InvariantCulture),
            LastUpdated = lastUpdated,
            Profile = profiles,
            Security =
            [
                new Coding(FhirR4Constants.TenantSecuritySystem, tenantId.Value)
            ]
        };

    private static Patient ToPatient(Domain.IdentityProfile profile)
    {
        var patient = new Patient
        {
            Active = true,
            BirthDate = profile.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Gender = MapGender(profile.Gender),
            Deceased = profile.IsDeceased ? new FhirBoolean(true) : null
        };
        patient.Identifier.AddRange(profile.Identifiers.Select(ToIdentifier));
        patient.Name.AddRange(profile.Names.Select(ToHumanName));
        patient.Address.AddRange(profile.Addresses.Select(ToAddress));
        patient.Telecom.AddRange(profile.Telecoms.Select(ToContactPoint));
        return patient;
    }

    private static void AddIdentityTags(
        Meta meta,
        IReadOnlyList<Domain.IdentityTag> tags)
    {
        meta.Tag.AddRange(tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Code))
            .Select(static tag => new Coding(tag.System, tag.Code, tag.Display))
            .Distinct());
    }

    private static Identifier ToIdentifier(Domain.IdentityIdentifier identifier)
    {
        var result = new Identifier
        {
            System = identifier.System,
            Value = identifier.Value,
            Use = ParseIdentifierUse(identifier.Use)
        };
        result.Extension.Add(new Extension(
            FhirR4Constants.IdentifierVerifiedExtension,
            new FhirBoolean(identifier.IsVerified)));
        result.Extension.Add(new Extension(
            FhirR4Constants.IdentifierAuthoritativeExtension,
            new FhirBoolean(identifier.IsAuthoritative)));
        return result;
    }

    private static HumanName ToHumanName(Domain.PersonName name)
    {
        var result = new HumanName
        {
            Family = name.Family,
            Use = MapNameUse(name.Use)
        };
        result.Given = name.Given;
        if (name.Prefix is not null)
        {
            result.Prefix = [name.Prefix];
        }

        if (name.Suffix is not null)
        {
            result.Suffix = [name.Suffix];
        }

        return result;
    }

    private static Address ToAddress(Domain.PostalAddress address) =>
        new()
        {
            Line = address.Lines,
            City = address.City,
            District = address.District,
            PostalCode = address.PostalCode,
            Country = address.Country,
            Use = MapAddressUse(address.Use)
        };

    private static Hl7.Fhir.Model.ContactPoint ToContactPoint(Domain.ContactPoint contact) =>
        new()
        {
            System = MapContactSystem(contact.System),
            Value = contact.Value,
            Use = ParseContactUse(contact.Use)
        };

    private static bool GetBooleanExtension(Element element, string url) =>
        element.Extension.FirstOrDefault(extension =>
            string.Equals(extension.Url, url, StringComparison.Ordinal))?.Value
            is FhirBoolean { Value: true };

    private static string? Join(IEnumerable<string?> values)
    {
        var joined = string.Join(" ", values.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return joined.Length == 0 ? null : joined;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;

    private static Domain.AdministrativeGender MapGender(
        Hl7.Fhir.Model.AdministrativeGender? gender) =>
        gender switch
        {
            Hl7.Fhir.Model.AdministrativeGender.Male => Domain.AdministrativeGender.Male,
            Hl7.Fhir.Model.AdministrativeGender.Female => Domain.AdministrativeGender.Female,
            Hl7.Fhir.Model.AdministrativeGender.Other => Domain.AdministrativeGender.Other,
            _ => Domain.AdministrativeGender.Unknown
        };

    private static Hl7.Fhir.Model.AdministrativeGender MapGender(
        Domain.AdministrativeGender gender) =>
        gender switch
        {
            Domain.AdministrativeGender.Male => Hl7.Fhir.Model.AdministrativeGender.Male,
            Domain.AdministrativeGender.Female => Hl7.Fhir.Model.AdministrativeGender.Female,
            Domain.AdministrativeGender.Other => Hl7.Fhir.Model.AdministrativeGender.Other,
            _ => Hl7.Fhir.Model.AdministrativeGender.Unknown
        };

    private static Domain.NameUse MapNameUse(HumanName.NameUse? use) =>
        use switch
        {
            HumanName.NameUse.Usual => Domain.NameUse.Usual,
            HumanName.NameUse.Official => Domain.NameUse.Official,
            HumanName.NameUse.Temp => Domain.NameUse.Temp,
            HumanName.NameUse.Nickname => Domain.NameUse.Nickname,
            HumanName.NameUse.Anonymous => Domain.NameUse.Anonymous,
            HumanName.NameUse.Old => Domain.NameUse.Old,
            HumanName.NameUse.Maiden => Domain.NameUse.Maiden,
            _ => Domain.NameUse.Unknown
        };

    private static HumanName.NameUse MapNameUse(Domain.NameUse use) =>
        use switch
        {
            Domain.NameUse.Usual => HumanName.NameUse.Usual,
            Domain.NameUse.Official => HumanName.NameUse.Official,
            Domain.NameUse.Temp => HumanName.NameUse.Temp,
            Domain.NameUse.Nickname => HumanName.NameUse.Nickname,
            Domain.NameUse.Anonymous => HumanName.NameUse.Anonymous,
            Domain.NameUse.Old => HumanName.NameUse.Old,
            Domain.NameUse.Maiden => HumanName.NameUse.Maiden,
            _ => HumanName.NameUse.Usual
        };

    private static Domain.AddressUse MapAddressUse(Address.AddressUse? use) =>
        use switch
        {
            Address.AddressUse.Home => Domain.AddressUse.Home,
            Address.AddressUse.Work => Domain.AddressUse.Work,
            Address.AddressUse.Temp => Domain.AddressUse.Temp,
            Address.AddressUse.Old => Domain.AddressUse.Old,
            Address.AddressUse.Billing => Domain.AddressUse.Billing,
            _ => Domain.AddressUse.Unknown
        };

    private static Address.AddressUse MapAddressUse(Domain.AddressUse use) =>
        use switch
        {
            Domain.AddressUse.Home => Address.AddressUse.Home,
            Domain.AddressUse.Work => Address.AddressUse.Work,
            Domain.AddressUse.Temp => Address.AddressUse.Temp,
            Domain.AddressUse.Old => Address.AddressUse.Old,
            Domain.AddressUse.Billing => Address.AddressUse.Billing,
            _ => Address.AddressUse.Home
        };

    private static Domain.ContactPointSystem MapContactSystem(
        Hl7.Fhir.Model.ContactPoint.ContactPointSystem? system) =>
        system switch
        {
            Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Phone => Domain.ContactPointSystem.Phone,
            Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Fax => Domain.ContactPointSystem.Fax,
            Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Email => Domain.ContactPointSystem.Email,
            Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Pager => Domain.ContactPointSystem.Pager,
            Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Url => Domain.ContactPointSystem.Url,
            Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Sms => Domain.ContactPointSystem.Sms,
            Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Other => Domain.ContactPointSystem.Other,
            _ => Domain.ContactPointSystem.Unknown
        };

    private static Hl7.Fhir.Model.ContactPoint.ContactPointSystem MapContactSystem(
        Domain.ContactPointSystem system) =>
        system switch
        {
            Domain.ContactPointSystem.Phone => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Phone,
            Domain.ContactPointSystem.Fax => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Fax,
            Domain.ContactPointSystem.Email => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Email,
            Domain.ContactPointSystem.Pager => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Pager,
            Domain.ContactPointSystem.Url => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Url,
            Domain.ContactPointSystem.Sms => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Sms,
            Domain.ContactPointSystem.Other => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Other,
            _ => Hl7.Fhir.Model.ContactPoint.ContactPointSystem.Other
        };

    private static Identifier.IdentifierUse? ParseIdentifierUse(string? use) =>
        Enum.TryParse<Identifier.IdentifierUse>(use, true, out var parsed) ? parsed : null;

    private static Hl7.Fhir.Model.ContactPoint.ContactPointUse? ParseContactUse(string? use) =>
        Enum.TryParse<Hl7.Fhir.Model.ContactPoint.ContactPointUse>(use, true, out var parsed)
            ? parsed
            : null;
}

using FhirMpi.Domain;

namespace FhirMpi.Portal;

public static class PortalDisplay
{
    public static string PatientName(IdentityProfile profile)
    {
        var name = profile.Names.FirstOrDefault(name => name.Use == NameUse.Official) ??
                   (profile.Names.Count > 0 ? profile.Names[0] : null);
        if (name is null)
        {
            return "Name unavailable";
        }

        return string.Join(
            ' ',
            name.Given.Append(name.Family)
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    public static string Initials(string value)
    {
        var parts = value.Split(
            ' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Take(2).Select(static part => char.ToUpperInvariant(part[0])));
    }

    public static string NhsNumber(IdentityProfile profile)
    {
        var value = profile.Identifiers.FirstOrDefault(identifier =>
            string.Equals(
                identifier.System,
                "https://fhir.nhs.uk/Id/nhs-number",
                StringComparison.Ordinal))?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Not recorded";
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 10
            ? $"{digits[..3]} {digits.Substring(3, 3)} {digits[6..]}"
            : value;
    }

    public static string BirthDate(DateOnly? date) =>
        date?.ToString("dd MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB")) ??
        "Not recorded";

    public static string Instant(DateTimeOffset instant) =>
        instant == DateTimeOffset.MinValue
            ? "Deployment configuration"
            : instant.ToLocalTime().ToString(
                "dd MMM yyyy, HH:mm",
                System.Globalization.CultureInfo.GetCultureInfo("en-GB"));

    public static string Gender(AdministrativeGender gender) =>
        gender == AdministrativeGender.Unknown ? "Not recorded" : gender.ToString();

    public static string Address(PostalAddress address) =>
        string.Join(
            ", ",
            address.Lines
                .Append(address.City)
                .Append(address.District)
                .Append(address.PostalCode)
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

    public static string ReviewStatus(ReviewCaseStatus status) =>
        status switch
        {
            ReviewCaseStatus.Pending => "Needs first review",
            ReviewCaseStatus.AwaitingSecondApproval => "Needs second approval",
            ReviewCaseStatus.Linked => "Linked",
            ReviewCaseStatus.Rejected => "Rejected",
            ReviewCaseStatus.Split => "Split completed",
            ReviewCaseStatus.Superseded => "Superseded",
            _ => status.ToString()
        };

    public static string ReviewKind(ReviewCaseKind kind) =>
        kind switch
        {
            ReviewCaseKind.PotentialDuplicate => "Automatic duplicate",
            ReviewCaseKind.ManualDuplicate => "Manual duplicate",
            ReviewCaseKind.Split => "Identity split",
            _ => kind.ToString()
        };

    public static string Grade(MatchGrade grade) =>
        grade == MatchGrade.None ? "Below threshold" : grade.ToString();
}

using System.Text;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Portal;

public static class SyntheticAssuranceDataset
{
    public const string DatasetId = "public-demo-calibration-v1";
    public const int MatchPairCount = 20;
    public const int NonMatchPairCount = 20;

    private static readonly (string Source, string Prefix)[] PartnerSources =
    [
        ("cardiff-and-vale", "CAV"),
        ("aneurin-bevan", "AB"),
        ("betsi-cadwaladr", "BCU"),
        ("cwm-taf-morgannwg", "CTM"),
        ("hywel-dda", "HD"),
        ("powys", "PTHB"),
        ("swansea-bay", "SBU"),
        ("velindre", "VEL")
    ];

    public static IReadOnlyList<SyntheticAssuranceIdentity> Identities { get; } =
        CreateIdentities();

    public static string Labels { get; } = CreateLabels();

    private static SyntheticAssuranceIdentity[] CreateIdentities() =>
        Enumerable.Range(1, MatchPairCount)
            .Select(sequence =>
            {
                var partner = PartnerSources[(sequence - 1) % PartnerSources.Length];
                var birthDate = new DateOnly(
                    1959 + sequence,
                    (sequence - 1) % 12 + 1,
                    (sequence - 1) % 20 + 1);
                var primaryFamily = $"Synthetic{sequence:D2}";
                var primaryGiven = $"Patient{sequence:D2}";
                var address = $"{99 + sequence} Assurance Way";
                var phone = $"0770091{sequence:D4}";
                return new SyntheticAssuranceIdentity(
                    new SourceRecordKey(
                        new SourceSystemId("wds"),
                        $"CAL-WDS-{sequence:D3}"),
                    new SourceRecordKey(
                        new SourceSystemId(partner.Source),
                        $"CAL-{partner.Prefix}-{sequence:D3}"),
                    primaryFamily,
                    sequence % 4 == 0
                        ? $"Synthetik{sequence:D2}"
                        : primaryFamily,
                    primaryGiven,
                    sequence % 3 == 0
                        ? $"Pat{sequence:D2}"
                        : primaryGiven,
                    birthDate,
                    sequence % 2 == 0
                        ? AdministrativeGender.Female
                        : AdministrativeGender.Male,
                    address,
                    sequence % 5 == 0
                        ? "Assurance Way"
                        : address,
                    "Demo City",
                    $"ZZ{(sequence - 1) % 9 + 1} {sequence % 10}ZZ",
                    phone,
                    sequence % 4 == 1 ? null : phone);
            })
            .ToArray();

    private static string CreateLabels()
    {
        var labels = new StringBuilder(
            "leftSource\tleftLocalId\trightSource\trightLocalId\tisMatch\n");
        foreach (var identity in Identities)
        {
            Append(labels, identity.Primary, identity.Partner, "match");
        }

        for (var index = 0; index < Identities.Count; index++)
        {
            var left = Identities[index].Primary;
            var right = Identities[(index + 7) % Identities.Count].Partner;
            Append(labels, left, right, "non-match");
        }

        return labels.ToString();
    }

    private static void Append(
        StringBuilder labels,
        SourceRecordKey left,
        SourceRecordKey right,
        string outcome) =>
        labels
            .Append(left.SourceSystem.Value).Append('\t')
            .Append(left.LocalId).Append('\t')
            .Append(right.SourceSystem.Value).Append('\t')
            .Append(right.LocalId).Append('\t')
            .Append(outcome).Append('\n');
}

public sealed record SyntheticAssuranceIdentity(
    SourceRecordKey Primary,
    SourceRecordKey Partner,
    string PrimaryFamily,
    string PartnerFamily,
    string PrimaryGiven,
    string PartnerGiven,
    DateOnly BirthDate,
    AdministrativeGender Gender,
    string PrimaryAddress,
    string PartnerAddress,
    string City,
    string Postcode,
    string PrimaryPhone,
    string? PartnerPhone);

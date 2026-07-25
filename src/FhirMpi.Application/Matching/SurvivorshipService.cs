using FhirMpi.Domain;

namespace FhirMpi.Application.Matching;

public sealed class SurvivorshipService
{
    public static IdentityProfile Merge(
        IdentityProfile current,
        int currentTrust,
        IdentityProfile incoming,
        int incomingTrust,
        DateTimeOffset? currentUpdated = null,
        DateTimeOffset? incomingUpdated = null,
        string? currentStableSourceId = null,
        string? incomingStableSourceId = null)
    {
        var incomingWins = CompareRank(
            currentTrust,
            incomingTrust,
            currentUpdated,
            incomingUpdated,
            currentStableSourceId,
            incomingStableSourceId) < 0;
        var primary = incomingWins ? incoming : current;
        var secondary = incomingWins ? current : incoming;

        return new IdentityProfile(
            MergeIdentifiers(primary.Identifiers, secondary.Identifiers),
            MergeDistinct(primary.Names, secondary.Names, PersonNameValueComparer.Instance),
            primary.BirthDate ?? secondary.BirthDate,
            primary.Gender != AdministrativeGender.Unknown ? primary.Gender : secondary.Gender,
            MergeDistinct(primary.Addresses, secondary.Addresses, PostalAddressValueComparer.Instance),
            MergeDistinct(primary.Telecoms, secondary.Telecoms),
            primary.IsDeceased || secondary.IsDeceased);
    }

    private static int CompareRank(
        int currentTrust,
        int incomingTrust,
        DateTimeOffset? currentUpdated,
        DateTimeOffset? incomingUpdated,
        string? currentStableSourceId,
        string? incomingStableSourceId)
    {
        var trustComparison = currentTrust.CompareTo(incomingTrust);
        if (trustComparison != 0)
        {
            return trustComparison;
        }

        var updatedComparison = Nullable.Compare(currentUpdated, incomingUpdated);
        if (updatedComparison != 0)
        {
            return updatedComparison;
        }

        // Lexically smaller stable IDs win the final tie so call order cannot change the result.
        return -string.Compare(
            currentStableSourceId ?? string.Empty,
            incomingStableSourceId ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static IdentityIdentifier[] MergeIdentifiers(
        IReadOnlyList<IdentityIdentifier> primary,
        IReadOnlyList<IdentityIdentifier> secondary) =>
        primary.Concat(secondary)
            .Distinct()
            .OrderByDescending(static identifier => identifier.IsVerified)
            .ThenByDescending(static identifier => identifier.IsAuthoritative)
            .ThenBy(static identifier => identifier.System, StringComparer.Ordinal)
            .ThenBy(static identifier => identifier.Value, StringComparer.Ordinal)
            .ToArray();

    private static T[] MergeDistinct<T>(
        IReadOnlyList<T> primary,
        IReadOnlyList<T> secondary) =>
        primary.Concat(secondary).Distinct().ToArray();

    private static T[] MergeDistinct<T>(
        IReadOnlyList<T> primary,
        IReadOnlyList<T> secondary,
        IEqualityComparer<T> comparer) =>
        primary.Concat(secondary).Distinct(comparer).ToArray();

    private sealed class PersonNameValueComparer : IEqualityComparer<PersonName>
    {
        public static PersonNameValueComparer Instance { get; } = new();

        public bool Equals(PersonName? left, PersonName? right) =>
            ReferenceEquals(left, right) ||
            left is not null &&
            right is not null &&
            string.Equals(left.Family, right.Family, StringComparison.Ordinal) &&
            left.Given.SequenceEqual(right.Given, StringComparer.Ordinal) &&
            left.Use == right.Use &&
            string.Equals(left.Prefix, right.Prefix, StringComparison.Ordinal) &&
            string.Equals(left.Suffix, right.Suffix, StringComparison.Ordinal);

        public int GetHashCode(PersonName value)
        {
            var hash = new HashCode();
            hash.Add(value.Family, StringComparer.Ordinal);
            foreach (var given in value.Given)
            {
                hash.Add(given, StringComparer.Ordinal);
            }

            hash.Add(value.Use);
            hash.Add(value.Prefix, StringComparer.Ordinal);
            hash.Add(value.Suffix, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed class PostalAddressValueComparer : IEqualityComparer<PostalAddress>
    {
        public static PostalAddressValueComparer Instance { get; } = new();

        public bool Equals(PostalAddress? left, PostalAddress? right) =>
            ReferenceEquals(left, right) ||
            left is not null &&
            right is not null &&
            left.Lines.SequenceEqual(right.Lines, StringComparer.Ordinal) &&
            string.Equals(left.City, right.City, StringComparison.Ordinal) &&
            string.Equals(left.District, right.District, StringComparison.Ordinal) &&
            string.Equals(left.PostalCode, right.PostalCode, StringComparison.Ordinal) &&
            string.Equals(left.Country, right.Country, StringComparison.Ordinal) &&
            left.Use == right.Use;

        public int GetHashCode(PostalAddress value)
        {
            var hash = new HashCode();
            foreach (var line in value.Lines)
            {
                hash.Add(line, StringComparer.Ordinal);
            }

            hash.Add(value.City, StringComparer.Ordinal);
            hash.Add(value.District, StringComparer.Ordinal);
            hash.Add(value.PostalCode, StringComparer.Ordinal);
            hash.Add(value.Country, StringComparer.Ordinal);
            hash.Add(value.Use);
            return hash.ToHashCode();
        }
    }
}

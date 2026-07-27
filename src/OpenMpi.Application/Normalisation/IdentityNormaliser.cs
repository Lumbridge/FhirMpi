using System.Globalization;
using System.Text;
using OpenMpi.Domain;

namespace OpenMpi.Application.Normalisation;

public sealed class IdentityNormaliser
{
    public static NormalisedIdentity Normalise(IdentityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var identifiers = profile.Identifiers
            .Where(static identifier =>
                !string.IsNullOrWhiteSpace(identifier.System) &&
                !string.IsNullOrWhiteSpace(identifier.Value))
            .Select(identifier => identifier with
            {
                System = identifier.System.Trim(),
                Value = NormaliseIdentifier(identifier.System, identifier.Value)
            })
            .Distinct()
            .ToArray();

        var names = profile.Names
            .Select(name =>
            {
                var family = NormaliseWords(name.Family);
                var given = name.Given
                    .Select(NormaliseWords)
                    .Where(static value => value.Length > 0)
                    .ToArray();
                return new NormalisedName(family, given, PhoneticEncoder.Encode(family));
            })
            .Where(static name => name.Family.Length > 0 || name.Given.Count > 0)
            .ToArray();

        var addresses = profile.Addresses
            .Select(address =>
            {
                var tokens = string.Join(
                    ' ',
                    address.Lines
                        .Append(address.City)
                        .Append(address.District)
                        .Where(static part => !string.IsNullOrWhiteSpace(part))
                        .Select(NormaliseWords));
                var postcode = NormalisePostcode(address.PostalCode);
                return new NormalisedAddress(tokens, postcode, GetPostcodeSector(postcode));
            })
            .Where(static address => address.AddressTokens.Length > 0 || address.PostalCode.Length > 0)
            .ToArray();

        var telecoms = profile.Telecoms
            .Select(contact => new NormalisedTelecom(
                contact.System,
                contact.System switch
                {
                    ContactPointSystem.Email => contact.Value.Trim().ToUpperInvariant(),
                    ContactPointSystem.Phone or ContactPointSystem.Fax or ContactPointSystem.Pager or ContactPointSystem.Sms =>
                        NormaliseTelephone(contact.Value),
                    _ => NormaliseWords(contact.Value)
                }))
            .Where(static contact => contact.Value.Length > 0)
            .Distinct()
            .ToArray();

        return new NormalisedIdentity(
            identifiers,
            names,
            profile.BirthDate,
            profile.Gender,
            addresses,
            telecoms);
    }

    public static string NormaliseWords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSeparator = true;

        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (Rune.IsLetterOrDigit(rune))
            {
                foreach (var character in rune.ToString().ToUpperInvariant())
                {
                    builder.Append(character);
                }

                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    public static string NormalisePostcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    public static string NormaliseTelephone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var hasInternationalPrefix = value.TrimStart().StartsWith('+');
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return hasInternationalPrefix ? $"+{digits}" : digits;
    }

    private static string NormaliseIdentifier(string system, string value)
    {
        if (string.Equals(system, NhsNumberValidator.IdentifierSystem, StringComparison.Ordinal))
        {
            return NhsNumberValidator.Normalise(value);
        }

        return value.Trim();
    }

    private static string GetPostcodeSector(string normalisedPostcode)
    {
        if (normalisedPostcode.Length < 5)
        {
            return string.Empty;
        }

        return normalisedPostcode[..^2];
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnifyEmpi.Application.Normalisation;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Application.Matching;

public sealed class BlockingKeyGenerator
{
    public static IReadOnlyList<BlockingKey> Generate(
        NormalisedIdentity identity,
        TenantMatchingConfiguration configuration)
    {
        var rawKeys = CreateRawKeys(identity, configuration.MatchingProfile);
        if (rawKeys.Count == 0)
        {
            throw new InsufficientIdentityDataException(
                "At least one enabled blocking rule must have all of its required identity fields.");
        }

        var results = new HashSet<BlockingKey>();
        foreach (var secret in configuration.BlockingKeySecrets)
        {
            foreach (var rawKey in rawKeys)
            {
                var digest = HMACSHA256.HashData(secret.Secret, Encoding.UTF8.GetBytes(rawKey));
                results.Add(new BlockingKey(secret.Version, Convert.ToHexString(digest)));
            }
        }

        return results.ToArray();
    }

    private static HashSet<string> CreateRawKeys(NormalisedIdentity identity, MatchingProfile profile)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (profile.BlockingRules.Contains(BlockingRuleKind.AuthoritativeIdentifier))
        {
            foreach (var identifier in identity.Identifiers.Where(identifier =>
                         profile.AuthoritativeIdentifierSystems.Contains(identifier.System)))
            {
                keys.Add($"ID|{identifier.System}|{identifier.Value}");
            }
        }

        if (identity.BirthDate.HasValue)
        {
            var birthDate = identity.BirthDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            foreach (var name in identity.Names.Where(static name => name.Family.Length > 0))
            {
                if (profile.BlockingRules.Contains(BlockingRuleKind.FamilyNameAndBirthDate))
                {
                    keys.Add($"FMDOB|{name.Family}|{birthDate}");
                }

                if (profile.BlockingRules.Contains(BlockingRuleKind.PhoneticFamilyNameAndBirthDate) &&
                    name.FamilyPhonetic.Length > 0)
                {
                    keys.Add($"PHDOB|{name.FamilyPhonetic}|{birthDate}");
                }
            }

            if (profile.BlockingRules.Contains(BlockingRuleKind.PostcodeAndBirthDate))
            {
                foreach (var address in identity.Addresses.Where(
                             static address => address.PostalCode.Length > 0))
                {
                    keys.Add($"PCDOB|{address.PostalCode}|{birthDate}");
                }
            }
        }

        foreach (var telecom in identity.Telecoms)
        {
            var enabled = telecom.System switch
            {
                ContactPointSystem.Phone or ContactPointSystem.Sms =>
                    profile.BlockingRules.Contains(BlockingRuleKind.Phone),
                ContactPointSystem.Email =>
                    profile.BlockingRules.Contains(BlockingRuleKind.Email),
                _ => false
            };
            if (enabled)
            {
                keys.Add($"TEL|{telecom.System}|{telecom.Value}");
            }
        }

        return keys;
    }
}

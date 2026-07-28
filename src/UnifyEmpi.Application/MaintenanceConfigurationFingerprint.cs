using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnifyEmpi.Domain;

namespace UnifyEmpi.Application;

internal static class MaintenanceConfigurationFingerprint
{
    public static string Create(TenantMatchingConfiguration configuration)
    {
        var profile = configuration.MatchingProfile;
        var builder = new StringBuilder()
            .Append(configuration.TenantId.Value).Append('\n')
            .Append(profile.Version).Append('\n')
            .Append(profile.PossibleThreshold.ToString("R", CultureInfo.InvariantCulture)).Append('\n')
            .Append(profile.ProbableThreshold.ToString("R", CultureInfo.InvariantCulture)).Append('\n')
            .Append(profile.MaximumCandidates.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(profile.DefaultResultCount.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(profile.MaximumResultCount.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(profile.Weights.FamilyName.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(profile.Weights.GivenNames.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(profile.Weights.BirthDate.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(profile.Weights.Address.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(profile.Weights.Telecom.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(profile.Weights.Gender.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var rule in profile.BlockingRules.Order())
        {
            builder.Append("rule:").Append(rule).Append('\n');
        }

        foreach (var system in profile.AuthoritativeIdentifierSystems.Order(StringComparer.Ordinal))
        {
            builder.Append("identifier:").Append(system).Append('\n');
        }

        builder.Append("comparators:")
            .Append(profile.Comparators.Version).Append('|')
            .Append(profile.Comparators.PhoneticMatchFloor.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(profile.Comparators.NicknameMatchFloor.ToString("R", CultureInfo.InvariantCulture)).Append('\n')
            .Append("family-comparators:")
            .AppendJoin(',', profile.Comparators.FamilyNameComparators)
            .Append('\n')
            .Append("given-comparators:")
            .AppendJoin(',', profile.Comparators.GivenNameComparators)
            .Append('\n');
        foreach (var dictionary in profile.Comparators.NicknameDictionaries
                     .OrderBy(static value => value.Version, StringComparer.Ordinal))
        {
            builder.Append("nickname-dictionary:")
                .Append(dictionary.Version).Append('|')
                .Append(dictionary.Culture).Append('\n');
            foreach (var entry in dictionary.EquivalenceKeys
                         .OrderBy(static value => value.Key, StringComparer.Ordinal))
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append('\n');
            }
        }

        if (profile.ProbabilityModel is { } model)
        {
            builder.Append("fellegi-sunter:")
                .Append(model.Version).Append('|')
                .Append(model.PriorMatchProbability.ToString("R", CultureInfo.InvariantCulture))
                .Append('|')
                .Append(model.TrainingDatasetDigest)
                .Append('\n');
            foreach (var field in model.Fields.OrderBy(static value => value.Field, StringComparer.Ordinal))
            {
                foreach (var level in field.Levels.OrderBy(static value => value.Level))
                {
                    builder.Append(field.Field).Append('|')
                        .Append(level.Level).Append('|')
                        .Append(level.MProbability.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                        .Append(level.UProbability.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        foreach (var secret in configuration.BlockingKeySecrets
                     .OrderBy(static secret => secret.Version, StringComparer.Ordinal))
        {
            var proof = HMACSHA256.HashData(
                secret.Secret,
                "unifyempi-maintenance-configuration-proof-v1"u8);
            builder.Append("secret:")
                .Append(secret.Version)
                .Append(':')
                .Append(secret.IsActive ? '1' : '0')
                .Append(':')
                .Append(Convert.ToHexString(proof))
                .Append('\n');
        }

        foreach (var source in configuration.SourceTrust
                     .OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
        {
            builder.Append("source:")
                .Append(source.Key.Value)
                .Append(':')
                .Append(source.Value.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(configuration.AuthoritativeSources.Contains(source.Key) ? '1' : '0')
                .Append('\n');
        }

        builder.Append("approvals:")
            .Append(configuration.RequiredLinkApprovals.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

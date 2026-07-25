using System.Security.Cryptography;
using System.Text;
using FhirMpi.Domain;

namespace FhirMpi.Application.Identifiers;

public static class StableResourceIdGenerator
{
    public static string Create(
        TenantId tenantId,
        SourceRecordKey sourceRecord,
        ReadOnlySpan<byte> secret)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{tenantId.Value}\n{sourceRecord.SourceSystem.Value}\n{sourceRecord.LocalId}");
        var digest = HMACSHA256.HashData(secret, input);
        return $"src-{Convert.ToHexString(digest[..16]).ToLowerInvariant()}";
    }
}

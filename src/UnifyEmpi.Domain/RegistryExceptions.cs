namespace UnifyEmpi.Domain;

public abstract class RegistryException(string message) : Exception(message);

public sealed class InsufficientIdentityDataException(string message) : RegistryException(message);

public sealed class CandidateLimitExceededException(int limit)
    : RegistryException($"The match input produced more than the allowed {limit} candidates.")
{
    public int Limit { get; } = limit;
}

public sealed class RegistryConcurrencyException(string message) : RegistryException(message);

public sealed class IdempotencyConflictException(string key)
    : RegistryException($"Idempotency key '{key}' was reused with different content.");

public sealed class RegistryNotFoundException(string resourceType, string id)
    : RegistryException($"{resourceType} '{id}' was not found.");

public sealed class RegistryAuthorisationException(string message) : RegistryException(message);

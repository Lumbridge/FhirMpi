using FhirMpi.Storage.Abstractions;
using FhirMpi.Storage.InMemory;
using FhirMpi.Storage.Testing;

namespace FhirMpi.Storage.ContractTests;

public sealed class InMemoryProviderContractTests : ProviderContractSuite
{
    protected override IIdentityRegistryStore CreateStore() =>
        new InMemoryIdentityRegistryStore();
}

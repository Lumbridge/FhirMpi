using OpenMpi.Storage.Abstractions;
using OpenMpi.Storage.InMemory;
using OpenMpi.Storage.Testing;

namespace OpenMpi.Storage.ContractTests;

public sealed class InMemoryProviderContractTests : ProviderContractSuite
{
    protected override IIdentityRegistryStore CreateStore() =>
        new InMemoryIdentityRegistryStore();
}

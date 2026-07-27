using UnifyEmpi.Storage.Abstractions;
using UnifyEmpi.Storage.InMemory;
using UnifyEmpi.Storage.Testing;

namespace UnifyEmpi.Storage.ContractTests;

public sealed class InMemoryProviderContractTests : ProviderContractSuite
{
    protected override IIdentityRegistryStore CreateStore() =>
        new InMemoryIdentityRegistryStore();
}

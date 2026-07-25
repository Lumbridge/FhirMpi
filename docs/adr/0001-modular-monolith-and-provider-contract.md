# ADR 0001: Modular monolith and provider contract

Status: accepted

FhirMpi uses a modular monolith with separate API and MLLP processes. Domain and application modules remain version- and provider-neutral. `IIdentityRegistryStore` is the only persistence boundary.

This keeps identity decisions transactional and explainable while allowing the two network protocols to scale independently. It avoids distributed transaction failure modes in the safety-critical link path. A deployment selects exactly one provider; runtime assembly hot-swapping is intentionally excluded.

Provider implementations must supply atomic mutations, optimistic concurrency, idempotency, opaque pagination, and bounded candidate lookup and must pass the common contract suite.

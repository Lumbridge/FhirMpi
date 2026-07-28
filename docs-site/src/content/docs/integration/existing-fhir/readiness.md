---
title: Source readiness assessment
description: Define tenant, source, identifier, provenance, security and data-quality contracts before importing an existing FHIR population.
---

Do not begin a full import until the source population can be mapped deterministically
to UnifyEMPI's tenant, source-system and local-patient identity model. The readiness
assessment should produce an approved interface contract, not just a successful
connectivity test.

## 1. Establish the governance boundary

Record:

- the data controller and processor responsibilities;
- the tenant that will contain the population;
- every organisation or independently governed feed represented in the source store;
- which service owns corrections, replacements and deletions;
- the permitted identity fields and retention period;
- named matching, review, information-governance and clinical-safety owners; and
- whether full-volume non-production processing is authorised.

UnifyEMPI never matches across tenants. A national deployment may use one governed
tenant with several organisation-level sources, but that is a governance decision
rather than a technical default.

## 2. Define source namespaces

Every imported Patient needs one stable source key:

```text
tenant / source system / source-local patient ID
```

For example:

```text
nhs-wales / cardiff-and-vale / CAV-123456
nhs-wales / wds / WDS-987654
```

Do not assign one generic source ID to a store that contains independently governed
records from several organisations. If local IDs can overlap, provenance must identify
the owning source before ingestion.

For the built-in external-FHIR reader, configure `LocalIdentifierSystem` when
`Patient.id` is not the governed source-local identifier. Every returned Patient must
then contain exactly one non-empty identifier with that system. Missing or multiple
values fail the batch rather than guessing.

## 3. Map the Patient contract

Inventory the source's actual R4 behaviour:

| Area | Questions to answer |
| --- | --- |
| Identity | Which identifiers are stable, recycled, replaced or organisation-scoped? |
| Demographics | Which names, addresses, telecoms, birth dates and gender values are populated? |
| Lifecycle | How are inactive, deceased, merged, replaced and entered-in-error Patients represented? |
| Versioning | Are `meta.versionId` and `meta.lastUpdated` reliable and monotonic? |
| Search | Does `Patient?_lastUpdated` support inclusive lower and upper bounds? |
| Paging | Are opaque `Bundle.link[relation=next]` URLs stable and same-origin? |
| Deletion | Is deletion available through history, subscription, event or Bulk Data metadata? |
| Validation | Which UK Core profiles, extensions and terminology constraints are used? |

Test the contract against real edge cases. A CapabilityStatement alone does not prove
that search, paging, versioning or identifier rules behave consistently at scale.

## 4. Govern NHS-number authority

An NHS number is authoritative only when all of these conditions are met:

- the identifier system is the configured NHS-number system;
- the number passes checksum validation;
- the source is configured as authoritative;
- the Patient carries a `traced` identity tag; and
- the Patient carries a `gold` identity tag.

The integration must preserve those two tags only when the upstream source provides
approved evidence for them. Do not infer traced or gold status from the presence of an
NHS number, from a preferred identifier use, or from the source organisation alone.

Test at least these cases:

| Scenario | Expected treatment |
| --- | --- |
| Valid NHS number, traced and gold, authoritative source | Eligible for authoritative matching |
| Valid NHS number without either tag | Demographic evidence only |
| Invalid checksum with both tags | Not authoritative |
| Traced and gold from a non-authoritative source | Not authoritative |
| Conflicting authoritative NHS numbers | Hard conflict and governed review |

## 5. Assess population quality

Produce counts by source and subgroup for:

- total, active, inactive and deleted Patients;
- missing and malformed source-local IDs;
- identifier duplication and collision;
- valid, invalid, traced and untraced NHS numbers;
- missing name, birth date, address, postcode and telecom;
- unusually common blocking combinations;
- duplicate source resource versions;
- late-arriving and out-of-order updates; and
- Patient records that fail the target validation profile.

Use these results to choose blocking rules, estimate candidate breadth and size the
review service. Common-name or low-information populations can hit the 500-candidate
safety guard and must be addressed through better source data or more selective
blocking.

## 6. Design access and network controls

The source reader should receive only the permissions needed to search and read
Patients. It does not need write permission on the existing store.

Confirm:

- HTTPS with approved certificates;
- private routing or approved ingress;
- short-lived credentials where supported;
- secrets held in a managed secret store;
- tenant and source identities issued from trusted claims;
- no Patient values in logs, metrics or exception text;
- source and MPI audit retention; and
- separate non-production credentials and blocking secrets.

Supported external-reader authentication modes are no authentication for controlled
development, a configured bearer token, and OAuth 2.0 client credentials. Production
integrations should normally use short-lived service credentials.

## Readiness exit criteria

Proceed to bootstrap only when:

- tenant, source and local-ID mappings are deterministic;
- traced NHS-number evidence has an approved mapping;
- snapshot, incremental-change and deletion semantics are understood;
- representative Patient samples pass validation;
- information-governance and clinical-safety owners approve the test boundary;
- throughput and source rate limits are documented;
- error quarantine and replay procedures exist; and
- source, MPI and reconciliation totals can be compared without exposing Patient data.

Next:
[bootstrap a large existing population](/UnifyEMPI/integration/existing-fhir/bootstrap/).

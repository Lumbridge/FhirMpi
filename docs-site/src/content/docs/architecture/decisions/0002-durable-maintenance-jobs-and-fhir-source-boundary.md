---
title: "ADR 0002: Durable maintenance jobs and FHIR source boundary"
description: Why re-indexing and reconciliation use leased, resumable jobs and import read-only snapshots from existing FHIR R4 servers.
---

**Status:** Accepted

## Context

Blocking-rule and HMAC-key changes must update every canonical index without creating a
period in which old or newly indexed records become undiscoverable. Existing source
updates do not re-run cross-cluster matching, so operators also need a repeatable way to
detect historical duplicates, repair derived registry state and incorporate records
from an established FHIR datastore.

These operations can outlive an HTTP request, encounter concurrent patient updates and
provider throttling, and move between replicas after deployment or failure. A remote
FHIR server is a source of authoritative snapshots, not a participant in UnifyEMPI's
atomic identity mutations.

## Decision

UnifyEMPI represents re-index and population-reconciliation work as durable,
tenant-bound FHIR `Task` resources behind `IIdentityRegistryStore`.

- A job stores its kind, phase, bounded cursor, counters, cancellation state,
  configuration fingerprint and non-PHI failure detail.
- Workers acquire expiring leases. Each batch renews the lease and checkpoints progress;
  another API replica may resume after expiry.
- Batch writes use optimistic concurrency and idempotent identifiers. Transient remote
  failures receive bounded retries; permanent failures preserve the checkpoint.
- Re-indexing validates old-to-target blocking-key overlap across the active population
  before changing a canonical Patient. Operators must deploy the union of old and new
  rules and retain the previous key during the transition.
- Reconciliation optionally imports external records, rebuilds source-derived canonical
  state and runs bounded blocking and matching for each canonical identity.
- Existing enterprise identities are never auto-merged by reconciliation. Deterministic
  candidate pairs become governed review Tasks.

The external-source adapter supports FHIR R4 Patient search only. It uses an inclusive
`_lastUpdated` lower bound, a fixed upper-bound instant, bounded `_count` pages and the
server's opaque `Bundle.link[relation=next]`. Next links must preserve the configured
scheme, authority and base path; redirects are disabled. Authentication is supplied
through protected configuration.

Imported Patients are persisted as source snapshots through the normal idempotent
ingestion boundary before they participate in matching. The adapter never writes back
to the source server and never interprets a missing search result as deletion.

## Consequences

- Re-index and reconciliation survive restarts and horizontal scaling without a
  separate queueing product.
- Progress, cancellation and failure are inspectable and auditable.
- Ordinary ingest and `$match` latency remain independent of population size.
- An online index migration needs a deliberate two-deployment union/final sequence.
- Scheduled reconciliation provides eventual cross-cluster assurance for updates, not
  immediate event-driven rematching.
- Hard deletion requires an explicit governed source event or a source-specific history
  or Bulk Data adapter.

## Rejected alternatives

- **Run the whole operation inside the initiating HTTP request.** This cannot provide
  safe restart, cancellation or replica failover.
- **Use an in-memory queue.** Work and checkpoints would be lost on process failure and
  duplicate across replicas.
- **Fall back to an unbounded patient-table comparison.** This makes latency and cost
  unpredictable and weakens the blocking safety boundary.
- **Perform live federation during matching.** Remote availability would enter the
  clinical decision path and could not participate in the registry's atomic mutation.
- **Write through to, or infer deletions from, the remote FHIR server.** Search absence
  is not reliable deletion evidence and source ownership remains outside UnifyEMPI.

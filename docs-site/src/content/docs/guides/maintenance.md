---
title: Re-indexing and population reconciliation
description: Run safe, resumable blocking-index migrations and scheduled population assurance, including ingestion from an existing FHIR R4 server.
---

UnifyEMPI runs re-indexing and population reconciliation as durable, tenant-scoped
background jobs. The API returns `202 Accepted`; job state is stored with the registry
and can be resumed by any API replica after a restart.

Jobs use bounded batches, optimistic concurrency and expiring worker leases. Their
configuration fingerprint prevents a job from continuing if matching rules, source
trust or blocking secrets change while it is running. Completion, cancellation and
failure are audited, and progress is emitted through the
`unifyempi.maintenance.jobs` and `unifyempi.maintenance.items` metrics.

## Operations portal workbench

Administrators can use **09 Maintenance** to run and inspect the same tenant-scoped
operations without constructing API requests. The page visualises each persisted job
as a phase pipeline and shows its validated, scanned, imported, updated, unchanged,
review-created, warning and failure counters.

Active jobs refresh automatically, can be cancelled at a safe batch boundary and
remain in the history after completion. The progress display intentionally does not
invent a percentage when the provider cannot supply a reliable population total. In
the public demo the controls operate only on synthetic records.

## Re-index compared with reconciliation

The operations solve different maintenance problems:

| Operation | What it refreshes | Use it when |
| --- | --- | --- |
| Re-index | Blocking keys stored on canonical Patients | Blocking rules, blocking normalisation or HMAC-key versions change |
| Population reconciliation | Canonical identity state, matching results and historical review candidates | Matching, comparator, threshold, source-authority or survivorship behaviour changes |

Re-indexing validates that the old and target blocking indexes overlap before writing
new blocking tags in bounded batches. It does not merge identities or reconsider every
historical match. Its purpose is to keep candidate discovery complete while blocking
configuration changes.

Reconciliation optionally imports changed Patients from a configured external FHIR
source, rebuilds canonical identities from their source records and then re-runs
bounded matching across the population. Probable duplicates become governed review
cases; the job never links two existing enterprise identities automatically.

Job counters describe work completed across phases rather than a distinct-patient
total. For example, a registry-only reconciliation of 46 identities can report 92
scanned items after visiting all 46 during both rebuilding and matching.

## Choosing the right operation

- **Matching weights, probability model, comparators or thresholds changed:** run
  reconciliation.
- **Blocking rules, blocking inputs or HMAC secrets changed:** stage the old/new union,
  run re-indexing and then run reconciliation.
- **Source trust, survivorship or authoritative-identifier handling changed:** run
  reconciliation; also re-index if the change affects a blocking input.
- **A source-system integration needs a controlled catch-up:** run reconciliation with
  the configured external source and an appropriate changed-since window.
- **Routine ingestion of one source Patient:** neither job is normally required; the
  ordinary ingestion path processes that record.
- **Periodic population assurance:** run scheduled reconciliation at the locally
  approved cadence.

## Safe online re-index

Blocking rule and HMAC-key changes must be staged:

1. deploy the **union** of the old and new blocking rules;
2. retain the previous HMAC key as inactive and configure the new key as active;
3. start a re-index job and wait for `completed`;
4. deploy the final rule set and remove the previous key; and
5. optionally run another re-index to remove obsolete stored tags.

Before changing a record, the validation phase proves that every existing active
canonical Patient has at least one blocking key shared by the old and target index. A
direct, non-overlapping replacement fails before any Patient is changed. This preserves
candidate discovery while old and newly indexed records coexist.

Start and inspect a job:

```http
POST /api/v1/maintenance/reindex
Content-Type: application/json
Authorization: Bearer <token with mpi.admin>

{
  "reason": "Approved addition of postcode blocking and v2 HMAC rotation.",
  "batchSize": 25
}
```

```http
GET /api/v1/maintenance/jobs/{jobId}
Authorization: Bearer <token with mpi.operations>
```

`batchSize` is limited to `1`–`25`, keeping durable FHIR transactions below practical
provider limits. Canonical Patient writes carry expected logical versions; concurrent
clinical updates cause the batch to retry rather than overwrite newer data.

## Population reconciliation

A population job has three phases:

1. **Importing** — optional changed-Patient ingestion from a configured external FHIR
   R4 server;
2. **Rebuilding** — recompute source authority, survivorship, Person links and blocking
   keys from the registry's source records; missing or inconsistent provenance is
   reported as a warning and is never deleted automatically; and
3. **Matching** — re-run bounded candidate discovery across the population and create
   governed `PopulationReconciliation` review cases for probable or certain duplicate
   canonical identities.

Reconciliation never auto-merges two existing enterprise identities. Review IDs are
deterministic for the tenant, identity versions and matching-profile version, so
retries and overlapping workers cannot create duplicate cases.

Start a registry-only run:

```http
POST /api/v1/maintenance/reconciliation
Content-Type: application/json
Authorization: Bearer <token with mpi.admin>

{
  "reason": "Monthly population assurance run.",
  "batchSize": 25
}
```

Add `externalSourceSystem` to import from an existing FHIR source before the registry
scan. `changedSince` may be supplied for a manual incremental window.

## Existing FHIR server integration

Each configured source performs an R4 `Patient` search with:

- an inclusive `_lastUpdated` lower bound for incremental runs;
- a fixed upper-bound instant captured when the job starts;
- a bounded `_count`; and
- the server's opaque `Bundle.link[relation=next]` URL.

Next-page URLs must retain the configured scheme, authority and base path. Redirects are
disabled so credentials cannot be forwarded to another origin. Replayed resource
versions use ingestion receipts and payload digests, making imports idempotent.

```json
{
  "Maintenance": {
    "WorkerEnabled": true,
    "PollIntervalSeconds": 5,
    "LeaseSeconds": 60,
    "FhirSources": [{
      "TenantId": "nhs-wales",
      "SourceSystem": "existing-epr",
      "BaseUrl": "https://fhir.example.nhs.uk/r4",
      "LocalIdentifierSystem": "https://fhir.example.nhs.uk/Id/patient",
      "PatientSearchParameters": {
        "active": "true"
      },
      "Authentication": {
        "Type": "ClientCredentials",
        "TokenEndpoint": "https://identity.example.nhs.uk/oauth2/token",
        "ClientId": "unifyempi",
        "ClientSecret": "<secret-store injection>",
        "Scope": "system/Patient.rs"
      }
    }],
    "ReconciliationSchedules": [{
      "Key": "existing-epr-nightly",
      "TenantId": "nhs-wales",
      "SourceSystem": "existing-epr",
      "IntervalMinutes": 1440,
      "BatchSize": 25,
      "RunOnStartup": true
    }]
  }
}
```

The external source-system ID must also exist in the tenant's `SourceTrust`
configuration. Supported authentication modes are `None`, a bearer token supplied
through protected configuration, and OAuth 2.0 client credentials. HTTPS is required;
the development-only `AllowInsecureHttp` switch must not be enabled in production.

FHIR search does not reliably communicate hard deletions. UnifyEMPI therefore treats
missing source records as warnings and never unlinks identities merely because a
resource is absent from a page. Deletion or replacement must arrive through an explicit
governed source event or a deployment-specific adapter that can consume the source
server's history or Bulk Data deletion feed.

## Scheduling and recovery

Schedules are per tenant and optionally per external source. A deterministic job ID is
derived from the schedule time bucket, preventing two API replicas from creating the
same scheduled run. Expired leases allow another replica to resume work. Transient
FHIR `408`, `429` and `5xx` responses use bounded exponential backoff; permanent errors
fail the job without exposing Patient values in the job error.

Cancel a queued or running job with:

```http
POST /api/v1/maintenance/jobs/{jobId}/cancel
Authorization: Bearer <token with mpi.admin>
```

Queued work is cancelled immediately. Running work observes the cancellation request at
the next batch boundary.

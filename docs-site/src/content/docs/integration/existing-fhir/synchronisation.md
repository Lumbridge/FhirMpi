---
title: Ongoing synchronisation
description: Configure incremental FHIR Patient ingestion, reconciliation schedules, paging safety, retries and deletion handling after bootstrap.
---

After bootstrap, keep UnifyEMPI current through the source's normal FHIR or HL7v2
events. Use scheduled external-FHIR reconciliation as a catch-up and assurance path
when the source exposes reliable `_lastUpdated` search behaviour.

## Configure an external FHIR source

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
      "RequestTimeoutSeconds": 60,
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

The source-system ID must also exist in the tenant's source-trust configuration.
Configure secrets through the deployment secret mechanism rather than committing them
to configuration files.

## Snapshot-window semantics

Every external reconciliation captures a fixed upper bound when the job starts. The
initial search is equivalent to:

```text
Patient
  ?_lastUpdated=ge<previous-completed-window-end>
  &_lastUpdated=le<this-job-window-end>
  &_count=<batch-size>
```

The lower bound is inclusive. Re-reading a boundary resource is expected and safe
because the ingestion receipt includes the source system, resource ID, source version
and payload digest.

The worker follows the server's opaque `Bundle.link[relation=next]`. A next link is
accepted only when it retains the configured scheme, authority and base path. Redirects
are disabled so a bearer token cannot be forwarded to another origin.

## Job phases

An external reconciliation runs:

1. **Importing** — read changed external Patients and submit them through the normal
   idempotent registry path.
2. **Rebuilding** — recalculate source authority, survivorship, `Person` links and
   blocking keys from stored source snapshots.
3. **Matching** — run bounded candidate discovery and create deterministic governed
   review cases for probable duplicates.

It never auto-merges two existing enterprise identities.

Use **09 Maintenance** in the operations portal or the maintenance API to inspect
phase, lease, attempts, cursors, imported, updated, unchanged, review, warning and
failure counters.

## Manual incremental run

```http
POST /api/v1/maintenance/reconciliation
Content-Type: application/json
Authorization: Bearer <token with mpi.admin>

{
  "reason": "Controlled catch-up after source outage.",
  "batchSize": 25,
  "externalSourceSystem": "existing-epr",
  "changedSince": "2026-07-27T00:00:00Z"
}
```

Use an approved overlap when recovering from an uncertain outage boundary. A replay is
preferable to a missed source update.

## Retry and recovery

- Jobs are stored in the registry rather than process memory.
- Workers acquire expiring leases, allowing another replica to resume after failure.
- FHIR `408`, `429` and `5xx` responses use bounded exponential backoff.
- Permanent authentication, mapping or validation errors fail the job without placing
  Patient values in its error text.
- A configuration fingerprint prevents the job from continuing after tenant matching,
  trust or blocking configuration changes mid-run.
- Cancellation takes effect at a batch boundary.

Investigate and correct the underlying problem before retrying a failed job. Do not
manually edit job cursors or stored source versions.

## Updates, replacements and deletions

FHIR `_lastUpdated` search is suitable for creates and updates but does not reliably
describe hard deletion. Absence from a search page is never treated as deletion and
does not unlink an identity.

Agree one of these deletion/replacement paths:

- an explicit governed FHIR update marking the Patient inactive or replaced;
- an HL7v2 identity event;
- a source history or subscription consumer;
- a Bulk Data deletion feed interpreted by a deployment-specific adapter; or
- a separately approved reconciliation process using a source manifest.

The adapter must preserve source ownership, expected versions and audit evidence.
Never infer that a Patient was deleted merely because it is missing from a partial
export.

## Operational monitoring

Alert on:

- increasing source-to-MPI lag;
- repeated job attempts or expired leases;
- import, validation or authentication failures;
- warnings about missing source provenance;
- candidate truncation;
- unexpected changes in updated/unchanged ratios;
- sudden review-volume changes;
- scheduled jobs that do not complete inside their interval; and
- drift between source, source-snapshot and canonical counts.

Use [re-indexing and population reconciliation](/UnifyEMPI/guides/maintenance/) when
configuration changes require a population refresh.

---
title: Validation and cut-over
description: Validate identity quality, safety, scale and recoverability before consumers depend on a newly onboarded FHIR population.
---

Complete validation in a shadow environment before directing production consumers to
UnifyEMPI. A successful import proves transport, not identity quality or clinical
safety.

## Shadow-operation model

During shadow operation:

- the existing FHIR server remains the operational source;
- normal source changes continue to flow;
- UnifyEMPI builds and maintains its identity graph;
- reviewers assess sampled and generated cases;
- no consumer treats the canonical identity as its sole clinical source; and
- rollback is simply stopping new traffic to UnifyEMPI while preserving evidence.

Do not write merge or survivorship outcomes back to the source store automatically.

## Reconcile the population

Compare aggregate counts for each tenant and source:

| Check | Expected evidence |
| --- | --- |
| Source snapshot coverage | Imported + unchanged + quarantined equals the approved source manifest |
| Source-key uniqueness | One source record per tenant/source/local-ID tuple |
| Version catch-up | No unexplained source version or `_lastUpdated` lag |
| Canonical population | Explained relationship between source records and active enterprise identities |
| Missing provenance | Zero, or an explicitly accepted and owned exception list |
| Inactive/replaced handling | Matches the approved lifecycle mapping |
| NHS-number authority | Only valid traced-and-gold identifiers from authoritative sources are trusted |
| Review workload | Volume and age meet the operating model |

Remember that maintenance `Scanned` counts include work across phases. A
reconciliation can scan every identity during rebuilding and again during matching;
that counter is not a distinct-patient total.

## Validate matching quality

Build a governed labelled dataset representing:

- every source system;
- common and uncommon names;
- incomplete and conflicting demographics;
- address and contact changes;
- traced, untraced, invalid and conflicting NHS numbers;
- expected cross-source duplicates;
- relevant demographic and equality subgroups; and
- the difficult cases near operational thresholds.

Use the [matching assurance workbench](/UnifyEMPI/guides/matching-assurance/) to measure:

- blocking recall;
- precision and recall at proposed thresholds;
- false-positive and false-negative examples;
- field discrimination;
- calibration quality where Fellegi–Sunter is enabled; and
- performance against an independent holdout.

Do not infer production precision from a deliberately balanced clerical sample. The
production match prior and sampling frame require separate approval.

## Validate scale and recovery

Run representative tests for:

- sustained ingestion and incremental catch-up;
- `$match` and search latency under concurrent load;
- maximum and typical blocking-candidate breadth;
- review queue growth and reviewer throughput;
- FHIR-store quotas, paging and transaction latency;
- worker restart and lease expiry;
- idempotent replay of completed partitions;
- backup and restore;
- blocking-key rotation and online re-index;
- reconciliation cancellation and restart; and
- complete environment teardown without changing the source store.

Use the [performance guide](/UnifyEMPI/development/performance/) for benchmark
methodology. A full-volume test environment containing identifiable data needs
production-grade monitoring and incident response.

## Go-live exit criteria

Require explicit approval for:

- tenant and source boundaries;
- source-local identifier mappings;
- traced NHS-number semantics;
- source trust and survivorship;
- blocking, comparators, model and decision thresholds;
- labelled matching-assurance results;
- expected review volumes and accountable owners;
- privacy, retention, security and clinical-safety evidence;
- operating procedures and on-call routes;
- reconciliation cadence and deletion handling;
- capacity and recovery tests; and
- rollback triggers.

The [production-readiness checklist](/UnifyEMPI/governance/production-readiness/) is
the minimum platform-level companion to the integration evidence.

## Controlled cut-over

1. Freeze the approved configuration version.
2. Complete the initial snapshot and incremental catch-up.
3. Run final population reconciliation.
4. Confirm no active migration, re-index or reconciliation jobs remain.
5. Reconcile source and MPI totals.
6. Activate consumers gradually, beginning with read-only or advisory uses.
7. Monitor lag, errors, candidate breadth, reviews and match outcomes.
8. Retain the previous consumer route for the approved rollback period.

If matching or source mapping behaves unexpectedly, stop new consumer traffic, keep
the source feed and evidence intact, restore the previous configuration where
applicable and reassess. Do not repair a population by manually editing canonical
Patients or product-owned links in the backing store.

## Production topology decision

A test store can become the production store only when it was provisioned, populated
and operated under production controls from the beginning and its promotion is
explicitly approved. Otherwise create a dedicated production UnifyEMPI store and repeat
the reproducible bootstrap and catch-up process.

Return to the
[existing FHIR integration overview](/UnifyEMPI/integration/existing-fhir/overview/).

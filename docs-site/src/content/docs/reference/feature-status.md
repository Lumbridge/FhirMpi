---
title: Feature status
description: A concise view of implemented UnifyEMPI capabilities and deliberate roadmap gaps.
---

This page distinguishes what the repository implements today from the work still needed
for broader production operation.

## Implemented

| Area | Current capability |
| --- | --- |
| Identity registry | Source Patient ingestion, UUIDv7 enterprise identities, Person links and canonical Patient survivorship |
| FHIR R4 | Patient create, update, read, search and `$match`; Person lookup; JSON/XML; ETags and `OperationOutcome` |
| HL7v2 | MLLP ingestion for ADT A01, A04, A08, A28, A31, A40 and A47 |
| Matching | Configurable bounded blocking, versioned nickname dictionaries, six name comparators, weighted or calibrated Fellegi–Sunter scores, identifier certainty and hard conflicts |
| Matching assurance | Tenant-bound clerical-label reports with blocking recall, confusion matrices, precision/recall intervals, field diagnostics and held-out probability calibration |
| Review | Explainable probable-match cases, merge, reject, unlink/split, stale-case detection and dual approval |
| Tenancy | Trusted tenant and source context on every identity, query, receipt, decision and audit event |
| Storage | Development in-memory provider and durable GCP Healthcare API R4 provider behind one contract |
| Operations | Blazor portal with duplicate, review, split, audit, configuration and admin-only matching-assurance workbenches; health checks and OpenTelemetry |
| Maintenance | Durable online re-index jobs, scheduled population reconciliation and incremental external FHIR Patient ingestion |
| Deployment | Containers, Compose, Helm, Terraform foundations and a reproducible GCP demo |

## Deliberate gaps

| Capability | Status |
| --- | --- |
| Adaptive or trainable ML classification | Not implemented |
| Arbitrary non-patient entity resolution | Out of scope |
| Broad webhook and integration catalogue | Not implemented |
| Certified clinical product status | Not claimed |

Blocking changes now use a guarded online migration: retain old keys and rules, stage
the union configuration, complete the durable re-index, and only then remove obsolete
inputs. See [re-indexing and reconciliation](/UnifyEMPI/guides/maintenance/) for the
operating sequence and external FHIR source configuration.

Matching changes can now be measured against tenant-bound clerical labels before
deployment. Calibration returns a versioned model but never activates it automatically;
see [matching assurance and calibration](/UnifyEMPI/guides/matching-assurance/).

Read the [matching rules](/UnifyEMPI/matching/rules/) for exact behaviour and
[production readiness](/UnifyEMPI/governance/production-readiness/) for the controls that remain
the deploying organisation's responsibility.

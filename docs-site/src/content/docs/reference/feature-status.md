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
| Matching | Configurable bounded blocking rounds, field weights, thresholds, identifier certainty and hard-conflict handling |
| Review | Explainable probable-match cases, merge, reject, unlink/split, stale-case detection and dual approval |
| Tenancy | Trusted tenant and source context on every identity, query, receipt, decision and audit event |
| Storage | Development in-memory provider and durable GCP Healthcare API R4 provider behind one contract |
| Operations | Blazor portal, source trust, policy editing, audit search, health checks and OpenTelemetry |
| Maintenance | Durable online re-index jobs, scheduled population reconciliation and incremental external FHIR Patient ingestion |
| Deployment | Containers, Compose, Helm, Terraform foundations and a reproducible GCP demo |

## Deliberate gaps

| Capability | Status |
| --- | --- |
| Ground-truth recall and precision reporting | Not implemented |
| Nickname dictionaries and additional comparator libraries | Not implemented |
| Fellegi–Sunter probability calibration | Not implemented |
| Adaptive or trainable ML classification | Not implemented |
| Arbitrary non-patient entity resolution | Out of scope |
| Broad webhook and integration catalogue | Not implemented |
| Certified clinical product status | Not claimed |

Blocking changes now use a guarded online migration: retain old keys and rules, stage
the union configuration, complete the durable re-index, and only then remove obsolete
inputs. See [re-indexing and reconciliation](/UnifyEMPI/guides/maintenance/) for the
operating sequence and external FHIR source configuration.

Read the [matching rules](/UnifyEMPI/matching/rules/) for exact behaviour and
[production readiness](/UnifyEMPI/governance/production-readiness/) for the controls that remain
the deploying organisation's responsibility.

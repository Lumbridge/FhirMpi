---
title: Production readiness
description: Safety, identity, infrastructure and governance work required before a production UnifyEMPI deployment.
---

UnifyEMPI provides an engineering foundation. A successful build or demo does not
establish clinical safety, regulatory approval or production
fitness.

## Required workstreams

1. **Clinical safety and governance** — define intended use, hazards, safety controls,
   ownership, escalation and release evidence.
2. **Data protection** — complete the applicable DPIA, lawful-basis analysis, retention
   design, data-sharing agreements and subject-rights process.
3. **Identity and access** — connect an approved OIDC issuer, issue tenant/source claims
   from trusted service identities, enforce least privilege and review access regularly.
4. **Matching assurance** — evaluate blocking recall and match precision against
   governed representative labels; assess subgroups, reviewer agreement and drift;
   approve thresholds, priors, nickname content and every profile version.
5. **Secure networking** — expose FHIR and portal endpoints through approved HTTPS
   ingress; keep MLLP private and require mutual TLS.
6. **Durability and recovery** — verify backups, restore, re-index, reconciliation,
   worker-lease recovery, idempotency, concurrency and disaster-recovery procedures.
7. **Secrets and cryptography** — store blocking HMAC keys, OIDC secrets and TLS material
   in managed secret stores; document rotation and re-index consequences.
8. **Operations** — establish monitoring, audit review, incident response, capacity
   planning, dependency scanning and change control.

## Deployment invariants

- Every operation has one trusted tenant.
- Source-system writes have one trusted source identity.
- Candidate discovery remains bounded; broad searches fail safely.
- Population iteration occurs only in durable background maintenance jobs; it never
  becomes an online matching fallback.
- External FHIR imports are read-only, same-origin paged and snapshot-based; absence is
  not treated as deletion.
- Organisation-owned source records are not edited through the operations portal.
- Matching-profile changes are deployed consistently to API, portal and MLLP hosts.
- Calibration output is never activated automatically, and assurance labels never cross
  tenant boundaries.
- Logs, traces, metrics and exceptions do not contain patient values.
- Demos contain synthetic data only.

## Before go-live

Treat the following as exit criteria rather than optional polish:

- matching and blocking acceptance evidence from a representative dataset, including
  blocking recall, precision/recall intervals, important subgroup results, an
  independent calibration holdout and an approved production match prior;
- threat model and penetration-test remediation;
- clinical-safety case and named safety ownership;
- approved operational runbooks and on-call routes;
- tested backup, restore, key-rotation, re-index, cancellation, lease-expiry and
  reconciliation procedures;
- an approved reconciliation cadence and accountable review-queue owners;
- approved, versioned nickname dictionaries or an explicit decision to keep them
  disabled;
- load and failure testing at representative scale;
- migration and rollback plans for product-owned identifiers; and
- explicit sign-off from the relevant information-governance, security and clinical
  authorities.

Use the [configuration reference](/UnifyEMPI/reference/configuration/), the normative
[matching rules](/UnifyEMPI/matching/rules/), the
[existing FHIR integration guides](/UnifyEMPI/integration/existing-fhir/overview/), the
[maintenance runbook](/UnifyEMPI/guides/maintenance/), the
[matching assurance guide](/UnifyEMPI/guides/matching-assurance/) and the
[security policy](/UnifyEMPI/governance/security/) alongside
your organisation's own standards.

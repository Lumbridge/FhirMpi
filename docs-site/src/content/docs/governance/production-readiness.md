---
title: Production readiness
description: Safety, identity, infrastructure and governance work required before a production UnifyEMPI deployment.
---

UnifyEMPI provides an engineering foundation. A successful build or synthetic
demonstration does not establish clinical safety, regulatory approval or production
fitness.

## Required workstreams

1. **Clinical safety and governance** — define intended use, hazards, safety controls,
   ownership, escalation and release evidence.
2. **Data protection** — complete the applicable DPIA, lawful-basis analysis, retention
   design, data-sharing agreements and subject-rights process.
3. **Identity and access** — connect an approved OIDC issuer, issue tenant/source claims
   from trusted service identities, enforce least privilege and review access regularly.
4. **Matching assurance** — evaluate blocking recall and match precision against governed
   representative data, approve thresholds and version every profile.
5. **Secure networking** — expose FHIR and portal endpoints through approved HTTPS
   ingress; keep MLLP private and require mutual TLS.
6. **Durability and recovery** — verify backups, restore, reconciliation, idempotency,
   concurrency and disaster-recovery procedures.
7. **Secrets and cryptography** — store blocking HMAC keys, OIDC secrets and TLS material
   in managed secret stores; document rotation and re-index consequences.
8. **Operations** — establish monitoring, audit review, incident response, capacity
   planning, dependency scanning and change control.

## Deployment invariants

- Every operation has one trusted tenant.
- Source-system writes have one trusted source identity.
- Candidate discovery remains bounded; broad searches fail safely.
- Organisation-owned source records are not edited through the operations portal.
- Matching-profile changes are deployed consistently to API, portal and MLLP hosts.
- Logs, traces, metrics and exceptions do not contain patient values.
- Demonstrations contain synthetic data only.

## Before go-live

Treat the following as exit criteria rather than optional polish:

- matching and blocking acceptance evidence;
- threat model and penetration-test remediation;
- clinical-safety case and named safety ownership;
- approved operational runbooks and on-call routes;
- tested backup, restore and key-rotation procedures;
- load and failure testing at representative scale;
- migration and rollback plans for product-owned identifiers; and
- explicit sign-off from the relevant information-governance, security and clinical
  authorities.

Use the [configuration reference](/UnifyEMPI/reference/configuration/), the normative
[matching rules](/UnifyEMPI/matching/rules/) and the
[security policy](/UnifyEMPI/governance/security/) alongside
your organisation's own standards.

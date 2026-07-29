---
title: Security policy
description: Supported versions, private vulnerability reporting and production security responsibilities.
---

For implemented controls, trust boundaries, permissions and deployment responsibilities,
see the [security architecture](/UnifyEMPI/governance/security-architecture/).

## Supported version

Security fixes target the latest commit on the default branch. The project does not yet
publish separately supported release lines.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting for this repository. Do not open a public
issue or discussion for a suspected vulnerability.

Include, where possible:

- the affected commit, component and deployment mode;
- a minimal reproduction using synthetic data;
- the security, tenant-isolation or patient-safety impact;
- relevant logs with all patient and credential values removed; and
- any suggested mitigation.

Do not include real patient information, access tokens, HMAC material, private keys,
certificates or production payloads. Do not test against the public demo in a
way that degrades availability, accesses another user's data or changes shared identity
links.

The maintainer will acknowledge the report through GitHub, assess severity and agree a
disclosure plan. This public project provides no contractual response-time guarantee.

## Production responsibility

UnifyEMPI provides technical security controls but is not a certified clinical product.
Deploying organisations remain responsible for threat modelling, penetration testing,
dependency and container scanning, key rotation, access reviews, incident response,
clinical safety, data protection and regulatory approval.

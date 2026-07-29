---
title: Contributing to UnifyEMPI
description: Development workflow and safety expectations for changes to identity resolution.
---

Contributions are welcome. UnifyEMPI handles identity-resolution concerns that can affect
patient safety, privacy and access, so changes should be small enough to review,
supported by evidence and explicit about their operational consequences.

## Before making a change

- Open or reference an issue for substantial matching, security, tenancy, persistence or
  protocol changes.
- Never use real patient or confidential information in fixtures, screenshots, issues,
  logs or demos.
- Read the [architecture](/UnifyEMPI/architecture/overview/),
  [core paths](/UnifyEMPI/architecture/core-paths/) and
  [concepts guide](/UnifyEMPI/concepts/identity-model/) before changing identity semantics.
- Report suspected vulnerabilities privately under the
  [security policy](/UnifyEMPI/governance/security/).

## Local development

Install the .NET 10 SDK selected by `global.json`. Docker is optional for the basic
build, but required for the Compose and container workflows. The
[developer guide](/UnifyEMPI/development/developer-guide/) contains the repository map,
per-host run and debug instructions, test-suite catalogue, dependency-update workflow
and common troubleshooting steps.

```powershell
dotnet restore UnifyEMPI.slnx --locked-mode
dotnet format UnifyEMPI.slnx --no-restore --verify-no-changes
dotnet build UnifyEMPI.slnx -c Release --no-restore
dotnet test UnifyEMPI.slnx -c Release --no-build
```

Run the local stack with:

```powershell
docker compose up --build
```

Local services use synthetic data and ephemeral in-memory providers. See the
[developer guide](/UnifyEMPI/development/developer-guide/#run-locally) for endpoints
and the important cross-host data-isolation limitation.

## Change expectations

- Preserve version-neutral domain and storage contracts; Firely and provider-native
  types must remain in adapters.
- Keep every operation tenant-bound. Do not add unscoped provider clients or accept
  tenant/source overrides from untrusted input.
- Preserve bounded candidate lookup and false-link safety. Matching changes need
  labelled synthetic fixtures and benchmark evidence.
- A new storage adapter must pass the shared provider-contract suite.
- Parser and normaliser changes need adversarial, boundary and malformed-input tests.
- Keep logs, traces, metrics and exceptions free of patient values.
- Use UK spelling in prose and user-facing text unless a protocol, resource or external
  name requires its published spelling.
- Update the relevant documentation and Postman examples when an interface changes.

## Pull requests

Explain:

- what changed and why;
- patient-safety, privacy, tenancy and compatibility effects;
- checks and benchmarks run;
- migration or deployment considerations; and
- any deliberately deferred work.

Keep generated build output, secrets, certificates, Terraform state and real patient
data out of commits. All checks in `.github/workflows/ci.yml` should pass before merge.

By contributing, you agree that your contribution is made available under the
repository's [CC0 1.0 dedication](https://github.com/Lumbridge/UnifyEMPI/blob/master/LICENSE).

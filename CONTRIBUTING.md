# Contributing to FhirMpi

Contributions are welcome. FhirMpi handles identity-resolution concerns that can affect
patient safety, privacy and access, so changes should be small enough to review,
supported by evidence and explicit about their operational consequences.

## Before making a change

- Open or reference an issue for substantial matching, security, tenancy, persistence or
  protocol changes.
- Never use real patient or confidential information in fixtures, screenshots, issues,
  logs or demonstrations.
- Read the [architecture](docs/architecture.md), [core paths](docs/core-paths.md) and
  [concepts guide](docs/concepts-and-faq.md) before changing identity semantics.
- Report suspected vulnerabilities privately under the [security policy](SECURITY.md).

## Local development

Install the .NET 10 SDK selected by `global.json`. Docker is optional for the basic
build, but required for the Compose and container workflows.

```powershell
dotnet restore FhirMpi.slnx --locked-mode
dotnet format FhirMpi.slnx --no-restore --verify-no-changes
dotnet build FhirMpi.slnx -c Release --no-restore
dotnet test FhirMpi.slnx -c Release --no-build
```

Run the local stack with:

```powershell
docker compose up --build
```

Local services use synthetic data and ephemeral in-memory providers. See the
[README](README.md#quick-start) for endpoints and limitations.

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
repository's [CC0 1.0 dedication](LICENSE).

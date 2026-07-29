---
title: Live GCP provider tests
description: Run destructive provider-contract tests safely against an isolated synthetic Healthcare API store.
---

This project is deliberately excluded from `UnifyEMPI.slnx`. It writes synthetic
resources and must run only against a short-lived, isolated Healthcare API R4
store supplied through `GCP_FHIR_STORE`, using Application Default Credentials.

The store name is the full Healthcare API resource name:

```powershell
$env:GCP_FHIR_STORE = "projects/PROJECT/locations/LOCATION/datasets/DATASET/fhirStores/STORE"
dotnet test tests/UnifyEmpi.Storage.Gcp.LiveTests/UnifyEmpi.Storage.Gcp.LiveTests.csproj `
  -c Release
```

The suite first checks provider health, atomic writes and tenant-safe reads, then runs
the same provider contract used by the in-memory and deterministic GCP adapter tests.
It leaves resources behind by design.

The routine `UnifyEmpi.Storage.Gcp.Tests` project does not need credentials and does
not contact GCP; it exercises the adapter against an in-process deterministic client.
Use that project for normal development.

:::caution
Destroy the disposable store after the live run. Never point this suite at a
production, shared, clinical or otherwise valuable store.
:::

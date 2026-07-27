---
title: Live GCP provider tests
description: Run destructive provider-contract tests safely against an isolated synthetic Healthcare API store.
---

This project is deliberately excluded from `UnifyEMPI.slnx`. It writes synthetic
resources and must run only against a short-lived, isolated Healthcare API R4
store supplied through `GCP_FHIR_STORE`, using Application Default Credentials.

```powershell
dotnet test tests/UnifyEmpi.Storage.Gcp.LiveTests -c Release
```

Destroy the test store after the run. Never point this suite at a production or
shared clinical store.

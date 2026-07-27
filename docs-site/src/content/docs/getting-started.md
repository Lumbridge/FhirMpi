---
title: Quick start
description: Run the UnifyEMPI API, operations portal and HL7v2 listener locally with synthetic data.
---

The local Compose stack is the fastest way to explore the full platform. It starts
three independently scalable hosts:

- `UnifyEmpi.Api` for FHIR R4, SMART discovery and reviewer APIs;
- `UnifyEmpi.Portal` for operational search, review and policy workflows; and
- `UnifyEmpi.Hl7v2.Host` for ADT ingestion over MLLP.

:::note[Prerequisites]
Install Docker Desktop. Install the .NET 10 SDK as well if you want to build or test
outside containers.
:::

## Start the stack

From the repository root:

```powershell
docker compose up --build
```

When all three hosts are healthy:

```powershell
Invoke-RestMethod http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/fhir/R4/metadata
Start-Process http://localhost:8081
```

| Surface | Local address |
| --- | --- |
| FHIR and review API | `http://localhost:8080` |
| Operations portal | `http://localhost:8081` |
| HL7v2 MLLP listener | `localhost:2575` |
| Development tenant | `demo` |
| Development source | `demo-source` |

The portal seeds six invented Patient records under WDS, Cardiff and Vale, Aneurin
Bevan and Velindre sources. These form three probable-match review cases.

## Understand the local boundary

The local stack disables authentication and gives each host its own ephemeral in-memory
provider. Data disappears when a host stops, and records sent to the API or MLLP host
are not visible in the portal. This is a developer demonstration, not a production
topology.

:::caution
Use invented data only. Never enter real patient, staff, credential or
organisation-confidential information.
:::

## Build and test the source

```powershell
dotnet restore UnifyEMPI.slnx --locked-mode
dotnet format UnifyEMPI.slnx --no-restore --verify-no-changes
dotnet build UnifyEMPI.slnx -c Release --no-restore
dotnet test UnifyEMPI.slnx -c Release --no-build
```

Stop the local containers with:

```powershell
docker compose down
```

## Next steps

- Learn the [identity model and terminology](/UnifyEMPI/concepts/identity-model/).
- Run the prepared [Postman matching tour](/UnifyEMPI/guides/postman/).
- Review [configuration](/UnifyEMPI/reference/configuration/) before changing tenants or sources.
- Explore the [public synthetic demonstration](/UnifyEMPI/deployment/public-demo/).

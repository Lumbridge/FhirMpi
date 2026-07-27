---
title: Postman collection
description: Run an importable FHIR discovery, canonical search and Patient matching tour against synthetic data.
---

Import
[`UnifyEMPI-Match-Demo.postman_collection.json`](/UnifyEMPI/downloads/UnifyEMPI-Match-Demo.postman_collection.json)
into Postman to explore the implemented API without assembling FHIR payloads manually.

The collection contains:

- readiness, FHIR `CapabilityStatement` and SMART discovery;
- canonical Patient search and read;
- JSON and XML `Patient/$match`;
- certain-only, empty-match and malformed-request examples;
- a local or authenticated source Patient create, ETag update and read sequence; and
- read-only operational, review, audit and tenant-settings requests.

Every request includes basic assertions, so the collection can also act as a smoke test.
Run folders in order where one request captures an identifier or ETag for the next.

## Variables and safety

| Variable | Default | Purpose |
| --- | --- | --- |
| `baseUrl` | `https://unifyempi-demo-api-mjpwolhr6q-nw.a.run.app` | Public demo discovery, canonical search and matching |
| `sourceBaseUrl` | `http://localhost:8080` | Mutating source-system examples |
| `operationsBaseUrl` | `http://localhost:8080` | Reviewer and operational reads |
| `accessToken` | Empty | Bearer token for a protected deployment |

The remaining variables are managed automatically by the collection.

The hosted demo and local development stack are unauthenticated and synthetic-only.
Never enter real patient or confidential information. The public default is used only
for non-mutating discovery, search and matching. Keep source-system writes and
operational requests pointed at an isolated local or approved protected deployment.

For local development:

```powershell
docker compose up --build
```

The API is then available on `http://localhost:8080` with development authentication
disabled. In a protected deployment, set `sourceBaseUrl` or `operationsBaseUrl` to the
approved API and provide a token containing the required trusted `tenant_id`,
`source_system` and scopes.

The collection does not include merge or split decisions because those operations
change enterprise links and require a case-specific rationale, version and governance
context. Use the operations portal for those workflows.

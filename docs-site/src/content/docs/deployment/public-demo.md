---
title: GCP demo deployment
description: Deploy, verify, operate and safely remove the shared UnifyEMPI demo.
---

The deployment script creates a branded UnifyEMPI demo in a chosen Google Cloud
project. The current shared demo is available at:

- Portal: <https://unifyempi-demo-mjpwolhr6q-nw.a.run.app>
- FHIR/API base: `https://unifyempi-demo-api-mjpwolhr6q-nw.a.run.app`
- [FHIR R4 CapabilityStatement](https://unifyempi-demo-api-mjpwolhr6q-nw.a.run.app/fhir/R4/metadata)

The script prints the assigned portal and API URLs after every successful deployment.

The portal opens as a demo reviewer in tenant `demo`. It includes six source
records, six enterprise identities and three explainable probable-match cases. The
workbench demonstrates patient creation and update in the non-authoritative `demo-ui`
source, patient search, provenance, survivorship, matching evidence, merge review,
rejection, corrective unlink/split, source trust, matching policy and audit history.

## Safety boundary

This deployment is intentionally unauthenticated so that it can be explored without an account. It must contain synthetic data only. Never submit real patient, staff, credential or organisation-confidential information.

When exposed publicly, visitors share one mutable demo tenant. Changes are
audited but can affect what later visitors see, and the store may be reset without
notice. The API uses a consumer identity for Patient search and `$match`; source-system
Patient writes are disabled in the demo API configuration. This deployment is
not a production reference for identity, availability, clinical safety or data
governance.

The public portal can create and update only records owned by its configured `demo-ui`
source. Health-board, WDS, Velindre and other organisation-owned source
records remain read-only. Create and update exist so the complete workflow can be
evaluated; use invented data only.

## Postman `$match` demo

Import [`UnifyEMPI-Match-Demo.postman_collection.json`](/UnifyEMPI/downloads/UnifyEMPI-Match-Demo.postman_collection.json)
into Postman and send **Match a synthetic patient**. Its default `baseUrl` points to
the shared demo API above. The collection contains a synthetic partial Patient,
FHIR media-type headers, the R4 `Parameters` wrapper, `onlyCertainMatches`, `count`,
and response tests for the searchset Bundle, score and `match-grade`.

The operation is read-only, but the public safety boundary still applies: never replace
the example with real patient information.

MLLP is not exposed to the public internet. HL7v2 MLLP carries raw TCP traffic and relies on a listener-bound tenant, source system and client identity, so the production design keeps it on a private endpoint with mutual TLS. The MLLP host remains available in Compose, Helm and the source tree.

## Default deployed topology

| Resource | Value |
|---|---|
| Project | Operator-supplied |
| Region | `europe-west2` unless overridden |
| Portal service | `unifyempi-demo` |
| API service | `unifyempi-demo-api` |
| Healthcare dataset | Operator-supplied |
| Dedicated R4 store | `unifyempi-demo` |
| Artifact Registry repository | `unifyempi-demo` |
| Runtime service account | `unifyempi-demo@{project}.iam.gserviceaccount.com` |
| Blocking-key secret | `unifyempi-demo-hmac` |

The dedicated FHIR store has resource history, referential integrity, update-create and complex reference parsing enabled. The service account has FHIR resource-editor access and secret access; it is not a project owner.

Cloud Run uses one vCPU and 1 GiB per instance. Both services scale to zero. The portal is capped at one instance to make initial synthetic seeding deterministic, while the API is capped at two. The portal uses session affinity for Interactive Server circuits.

## Deploy or update

Prerequisites are Google Cloud CLI, Docker and a billing-enabled project. Authenticate with `gcloud auth login` and run from the repository root:

```powershell
./scripts/Deploy-GcpPublicDemo.ps1 `
  -ProjectId YOUR_PROJECT `
  -Region europe-west2 `
  -Dataset YOUR_DATASET
```

The script creates only resources bearing the `unifyempi-demo` name, creates a cryptographically random blocking-key secret without printing it, publishes immutable images, deploys both Cloud Run services and verifies readiness. It reuses the named Healthcare dataset when it already exists.

To update an existing deployment, run the same command. The script publishes a new immutable timestamped image tag and creates new Cloud Run revisions. It does not erase an existing demo store.

## Verification

The deployment acceptance checks cover:

- liveness and readiness for both Cloud Run services;
- rendered synthetic-data warning and tenant-bound reviewer session;
- FHIR R4 CapabilityStatement and SMART discovery;
- canonical Patient search in FHIR JSON and XML;
- Patient `$match` in FHIR JSON and XML, including score and match grade;
- the GCP-backed operational summary and review queue;
- opening an explainable duplicate workbench with the correct subject and survivor;
- creating and updating a synthetic portal-owned patient with version-checked writes;
- desktop and 375-pixel responsive layouts with no page-level horizontal overflow; and
- GCP persistence with opaque backend ETags and separate logical registry versions.

## Cost controls

Scale-to-zero removes idle Cloud Run instance time, but it does not make the whole deployment free. Healthcare API storage and requests, Artifact Registry image storage, outbound data and logging can still incur charges. Current rates are published in the [Cloud Run pricing](https://cloud.google.com/run/pricing), [Cloud Healthcare API pricing](https://cloud.google.com/healthcare-api/pricing) and [Artifact Registry pricing](https://cloud.google.com/artifact-registry/pricing) pages.

Set a project budget and alert in Cloud Billing before promoting the link widely. No budget or billing alert is created automatically because those controls belong to the billing-account owner.

## Remove the demo

Review the exact names in the removal script, then run:

```powershell
./scripts/Remove-GcpPublicDemo.ps1 `
  -ProjectId YOUR_PROJECT `
  -Region europe-west2 `
  -Dataset YOUR_DATASET `
  -ConfirmRemoval
```

This deletes the two Cloud Run services, the dedicated FHIR store, secret, image
repository and runtime service account. The supplied pre-existing dataset, other
stores, project, billing configuration and enabled APIs are deliberately preserved.

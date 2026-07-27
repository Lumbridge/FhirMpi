# UnifyEMPI

[![CI](https://github.com/Lumbridge/UnifyEMPI/actions/workflows/ci.yml/badge.svg)](https://github.com/Lumbridge/UnifyEMPI/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![FHIR R4](https://img.shields.io/badge/FHIR-R4-E34A6F)](https://hl7.org/fhir/R4/)
[![Licence: CC0 1.0](https://img.shields.io/badge/licence-CC0--1.0-lightgrey.svg)](LICENSE)
[![Documentation](https://img.shields.io/badge/docs-Starlight-0F766E)](https://lumbridge.github.io/UnifyEMPI/)
[![Live demo](https://img.shields.io/badge/demo-live-148A08)](https://unifyempi-demo-mjpwolhr6q-nw.a.run.app)
[![Swagger UI](https://img.shields.io/badge/API-Swagger_UI-85EA2D?logo=swagger&logoColor=black)](https://unifyempi-demo-api-mjpwolhr6q-nw.a.run.app/)

UnifyEMPI is a high-performance, multi-tenant master patient index (MPI) foundation for FHIR R4 and HL7v2. It is a modular monolith with three independently scalable hosts:

- `UnifyEmpi.Api`: FHIR R4, SMART discovery, and reviewer APIs.
- `UnifyEmpi.Hl7v2.Host`: native MLLP ingestion for ADT identity events.
- `UnifyEmpi.Portal`: an enterprise operations portal using Blazor Interactive Server and generic OIDC.

The registry is hybrid. Source systems remain authoritative for their Patient snapshots; UnifyEMPI owns enterprise UUIDv7 identities, Person links, candidate indexes, canonical Patient views, review cases, receipts, and audit evidence.

> [!IMPORTANT]
> UnifyEMPI is an engineering foundation, not a certified clinical product. Demo
> deployments must contain synthetic data only. Production NHS use requires the independent
> safety, privacy, security, operational and contractual work described below.

See [core paths and processing model](https://lumbridge.github.io/UnifyEMPI/architecture/core-paths/) for annotated diagrams of real-time duplicate detection, FHIR and HL7 ingestion, `$match`, merge, split, survivorship, tenant isolation, and provider boundaries.
For the operational meaning of tenants, source Patients, canonical Patients, `Person`,
HMAC blocking tags and review errors, start with
[identity concepts and frequently asked questions](https://lumbridge.github.io/UnifyEMPI/concepts/identity-model/).

## Capabilities out of the box

- **Master patient index:** ingest source Patient records, create enterprise identities, maintain Person links, and serve a survivorship-based canonical Patient view.
- **FHIR R4 API:** Patient create, update, read, search, and `$match`; Person lookup; JSON and XML; ETags; structured `OperationOutcome` errors; and bounded UK Core validation.
- **Native HL7v2 ingestion:** MLLP support for ADT A01, A04, A08, A28, A31, A40, and A47 messages from HL7 versions 2.3.1, 2.4, and 2.5.1.
- **Safe, explainable matching:** configurable blocking rounds, field weights and
  thresholds; NHS-number validation; HMAC-protected candidate indexes; field-level
  evidence; and no population-wide scans.
- **Enterprise operations portal:** tenant dashboard, optional governed create/update
  for a separately configured UI-managed source, patient registry search, explainable
  duplicate workbench, governed merge and source-level unlink/split workflows, review
  queue, source trust, audited policy configuration, and audit search.
- **Review workflow:** probable matches create review cases that authorised reviewers can inspect, link, reject, correct, or close as superseded when an identity is replaced or its evidence changes. Tenant-configurable dual approval defaults to two distinct reviewers, with no self-approval.
- **Multi-tenant isolation:** every identity, query, decision, receipt, and audit event is bound to a trusted tenant and source-system context.
- **Replaceable storage:** an ephemeral in-memory provider for development and a durable GCP Healthcare API provider are included behind the same storage contract.
- **Production foundations:** external OIDC and SMART scopes, mutual-TLS MLLP, health checks, OpenTelemetry, JSON logging, containers, Compose, and a Helm chart.

## Demo

There is one hosted demo. It consists of two services backed by the same dedicated
FHIR store:

- [Operations portal](https://unifyempi-demo-mjpwolhr6q-nw.a.run.app)
- [Swagger API explorer](https://unifyempi-demo-api-mjpwolhr6q-nw.a.run.app/)
  (API base: `https://unifyempi-demo-api-mjpwolhr6q-nw.a.run.app`)

The Compose quick start below runs a separate local development stack; it is not a
second hosted demo.

The repository includes the reproducible GCP deployment that creates those
`unifyempi-demo` resources and prints the assigned Cloud Run URLs after deployment.
Never enter real patient or confidential information into a demo environment.
Public MLLP is deliberately excluded because production HL7v2 ingress must use a
private endpoint and mutual TLS.

See the [public-demo guide](https://lumbridge.github.io/UnifyEMPI/deployment/public-demo/) for the GCP topology, verified scenarios,
cost controls, reproducible deployment and safe teardown.

## Quick start

The quickest way to run both services is with Docker and Docker Compose:

```powershell
docker compose up --build
```

Once the containers are ready:

```powershell
Invoke-RestMethod http://localhost:8080/health/ready
Invoke-RestMethod http://localhost:8080/fhir/R4/metadata
Start-Process http://localhost:8081
```

- FHIR and review API: `http://localhost:8080`
- Swagger UI: `http://localhost:8080/`
- Operations portal: `http://localhost:8081`
- HL7v2 MLLP listener: `localhost:2575`
- Development tenant: `demo`
- Development source system: `demo-source`

The local stack deliberately disables authentication and uses an isolated ephemeral in-memory provider in each host. The portal seeds synthetic patients so its workflows can be explored immediately. Data is discarded when a host stops, and records ingested through the API or MLLP host are not shared with the portal in this development mode. This is not suitable for production.

## Operations portal

The portal is a server-rendered operations surface; patient data and OIDC tokens are not placed in browser storage. One validated `tenant_id` is active for the entire signed-in session, and every application call is constructed from that trusted claim.

Out of the box, authorised staff can:

- optionally create and update Patient records owned by a separate server-configured
  UI-managed source, with NHS-number validation and optimistic concurrency protection;
- find canonical patients using a strong identifier or family name with date of birth;
- compare canonical and source demographics with provenance and survivorship trust;
- run a bounded duplicate search and inspect the evidence behind every score;
- open a governed merge review, where the selected candidate is clearly identified as the surviving enterprise identity;
- unlink selected source records into a new identity through the corrective split workflow;
- approve or reject work with a required rationale and optimistic concurrency protection;
- recognise outdated reviews automatically, explain redirects or version changes, and close superseded work with immutable audit evidence;
- inspect source authority and trust, audit events, operational health, and review workload; and
- update non-secret tenant matching policy with an immutable audit event.

Run it directly with:

```powershell
dotnet run --project src/UnifyEmpi.Portal
```

The development configuration signs in a demo reviewer for tenant `demo`, seeds synthetic records, and uses no real patient data.

To build and test the source locally, install the .NET 10 SDK and run:

```powershell
dotnet restore UnifyEMPI.slnx --locked-mode
dotnet build UnifyEMPI.slnx -c Release --no-restore
dotnet test UnifyEMPI.slnx -c Release --no-build
```

Stop the local containers with `docker compose down`.

To explore discovery, canonical search, JSON/XML `$match`, safe error cases, local source
writes and reviewer reads without assembling requests, use the
[Postman collection and guide](https://lumbridge.github.io/UnifyEMPI/guides/postman/). Public requests target the
demo API by default; mutating requests deliberately target localhost.

## Performance and benchmarking

UnifyEMPI protects performance at three different levels; each test answers a different
question and the results should not be mixed:

| Level | What it measures | Current gate or target |
| --- | --- | --- |
| Core scoring benchmark | One in-process comparison against 500 already-normalised candidates, without network or storage time | CI baseline mean: **0.629 ms** on the hosted Ubuntu runner; hard ceiling: **10 ms**; fail on a regression greater than **10%** |
| End-to-end `$match` load test | HTTP, authentication, candidate lookup, scoring, serialisation and the configured provider | Acceptance target: **250 requests/second for 30 minutes**, **p95 below 250 ms**, and **under 0.1%** failed requests |
| Registry scale test | Candidate blocking and matching against a realistically populated durable registry | Acceptance target: **10 million canonical identities** using synthetic data in an isolated test store |

The core benchmark runs on every default CI build. Its checked-in number is a
BenchmarkDotNet `ShortRun` mean for regression detection, not a p95/p99 result and not
an end-to-end throughput claim. The k6 and 10-million-identity figures are release
acceptance targets that require a representative, same-region GCP environment; they
have not been demonstrated merely by running the demo.

Run the scoring benchmark and its regression gate with:

```powershell
dotnet run --project benchmarks/UnifyEmpi.Benchmarks -c Release -- `
  --filter "*ScoreFiveHundred*" --job short --exporters json
./scripts/Test-BenchmarkRegression.ps1
```

Run the end-to-end scenario only against an isolated synthetic store:

```powershell
$env:BASE_URL = "https://unifyempi.example"
$env:ACCESS_TOKEN = "<short-lived system token>"
k6 run benchmarks/k6/match.js
```

Performance comes primarily from bounded work: ingestion precomputes HMAC blocking
keys, candidate lookup never scans the patient population, at most 500 normalised
candidates reach the scoring engine, and API, MLLP and portal hosts scale independently.
See [the benchmark guide](https://lumbridge.github.io/UnifyEMPI/development/performance/) and
[core-path diagrams](https://lumbridge.github.io/UnifyEMPI/architecture/core-paths/) for methodology and data flow.

## Documentation map

- [Identity model and frequently asked questions](https://lumbridge.github.io/UnifyEMPI/concepts/identity-model/): tenants,
  national deployment, source/canonical records, blocking, reviews, existing-store
  onboarding and troubleshooting.
- [NHS Wales source model](https://lumbridge.github.io/UnifyEMPI/concepts/nhs-wales-sources/):
  organisation-level sources for all seven health boards, WDS and Velindre.
- [Matching and blocking rules](https://lumbridge.github.io/UnifyEMPI/matching/rules/): normative
  normalisation, configurable blocking rounds, score formula, certainty/conflict
  rules, routing, examples and feature-alignment gaps.
- [Core paths and processing model](https://lumbridge.github.io/UnifyEMPI/architecture/core-paths/): annotated diagrams for
  ingestion, matching, merge, split, survivorship and tenant isolation.
- [Architecture](https://lumbridge.github.io/UnifyEMPI/architecture/overview/): dependency boundaries, materialisation and the
  replaceable provider contract.
- [Configuration](https://lumbridge.github.io/UnifyEMPI/reference/configuration/): API, portal, OIDC, tenant, HMAC and MLLP
  settings.
- [GCP demo deployment](https://lumbridge.github.io/UnifyEMPI/deployment/public-demo/): deployable topology, demo scenarios,
  cost controls and safe teardown.
- [Postman collection](https://lumbridge.github.io/UnifyEMPI/guides/postman/): importable discovery, matching, source
  write and reviewer-operation examples.
- [Contributing](CONTRIBUTING.md) and [security policy](SECURITY.md): development
  expectations and private vulnerability reporting.
- [Storage-provider ADR](https://lumbridge.github.io/UnifyEMPI/architecture/decisions/0001-modular-monolith-and-provider-contract/): why
  the modular monolith and provider contract were selected.

## Moving to production

Do not promote the Compose development settings directly. A production deployment needs durable storage, real identity and access control, managed secrets, encrypted endpoints, validation packages, monitoring, and an agreed clinical and information-governance process.

1. **Define the tenant boundary.** Decide which organisation or data controller each tenant represents, which source systems belong to it, which identifiers are authoritative, and whether a shared deployment provides sufficient isolation.
2. **Provision durable infrastructure.** Select `GcpHealthcare` and create an R4 FHIR store with versioning, referential integrity, update-create, and strict search handling. The reference GCP deployment creates a private GKE cluster, Healthcare API dataset and store, and a Workload Identity service account:

   ```powershell
   terraform -chdir=deploy/terraform init
   terraform -chdir=deploy/terraform plan `
     -var="project_id=YOUR_PROJECT" `
     -var="gke_network=YOUR_NETWORK_SELF_LINK" `
     -var="gke_subnetwork=YOUR_SUBNETWORK_SELF_LINK"
   terraform -chdir=deploy/terraform apply
   ```

3. **Publish immutable images.** Build the API, MLLP and portal Dockerfiles, scan all three images, generate an SBOM, and push versioned tags or digests to your approved registry. Do not deploy the mutable `latest` tag.
4. **Configure identity, tenants, and secrets.** Connect the API and portal to an external OIDC issuer. API service credentials must supply trusted `tenant_id` and `source_system` claims; interactive portal identities must supply exactly one trusted `tenant_id` plus the required MPI permissions. Welsh health-board, WDS, Velindre and other organisation-owned records remain read-only in the portal. Grant `mpi.patient.write` only if the deployment deliberately adds a separate `Portal:ManagedSourceSystem` for UI-created records. Register the portal as an authorisation-code client with PKCE and a server-side client secret. Supply tenant definitions, source trust, authoritative sources, and at least one active 256-bit HMAC secret through a managed secret store; never commit secrets to a values file.
5. **Install validation packages.** Fetch the pinned FHIR R4 and UK Core packages, scan them, and make them available to the API through the read-only validation-package volume:

   ```powershell
   ./scripts/Get-FhirPackages.ps1 -Destination ./artefacts/fhir-packages
   ```

6. **Configure private HL7 ingress.** Give every MLLP listener a fixed tenant and source-system binding, a server certificate, and an explicit client-certificate allow-list. Keep `AllowPlaintext=false` and expose MLLP only through a private endpoint or an approved TLS load balancer.
7. **Deploy with Helm.** Copy the supplied values file, replace the example image locations, set the GCP store, OIDC issuer, Workload Identity service account, resource limits, and secret references, then deploy:

   ```powershell
   helm upgrade --install unifyempi deploy/helm/unifyempi `
     --namespace unifyempi `
     --create-namespace `
     --values production-values.yaml
   ```

   Keep the Helm namespace aligned with Terraform's `kubernetes_namespace` value. The chart expects you to provide the validation-package PVC, a shared portal Data Protection key PVC, configuration and MLLP TLS Secrets, trusted HTTPS ingress, certificate management, and any cloud-specific private load balancer. The portal uses Blazor Interactive Server, so its ingress must support WebSockets and session affinity; the chart also enables `ClientIP` service affinity. Encrypt and restrict the shared Data Protection volume.

8. **Verify before admitting patient data.** Confirm `/health/live`, `/health/ready`, and `/fhir/R4/metadata`; run synthetic FHIR and HL7 smoke tests; test tenant isolation, JWT scopes, client certificates, audit delivery, alerts, scaling, backup/restore, and failure recovery.

Production NHS use additionally requires an approved DPIA, clinical-safety case, penetration test, data-retention and disaster-recovery policies, operational ownership, and contractual/compliance review. UnifyEMPI supplies technical controls but does not itself provide production approval or certification.

Detailed settings are documented in the [configuration reference](https://lumbridge.github.io/UnifyEMPI/reference/configuration/). Use
[identity concepts and frequently asked questions](https://lumbridge.github.io/UnifyEMPI/concepts/identity-model/) when selecting a
tenant boundary or planning an existing-store onboarding. The portable chart is under
[deploy/helm/unifyempi](deploy/helm/unifyempi), and the optional GCP reference
infrastructure is under [deploy/terraform](deploy/terraform).

## Supported interfaces

FHIR R4:

- `GET /fhir/R4/metadata`
- `GET /.well-known/smart-configuration`
- Patient create, update, read, and canonical search
- Person read and search for reviewer scopes
- `POST /fhir/R4/Patient/$match`

Reviewer API:

- `GET /api/v1/review-cases`
- `GET /api/v1/review-cases/{id}`
- `POST /api/v1/review-cases/{id}/decisions`
- `POST /api/v1/review-cases/manual-duplicate`
- `POST /api/v1/review-cases/split`
- `GET /api/v1/registry/patients/{id}`
- `GET /api/v1/registry/patients/{id}/duplicates`
- `GET /api/v1/operations/summary`
- `GET /api/v1/audit-events`
- `GET` and `PUT /api/v1/tenant/settings`

HL7v2:

- Versions 2.3.1, 2.4, and 2.5.1
- ADT A01, A04, A08, A28, A31, A40, and A47
- MLLP over explicitly enabled plaintext (development only) or mutual TLS

## Provider selection

Set exactly one `RegistryProvider:Type`:

- `InMemory`: ephemeral development and provider-conformance tests.
- `GcpHealthcare`: GCP Healthcare API R4 using Application Default Credentials / Workload Identity.

The application depends only on `IIdentityRegistryStore`; Firely resources, GCP URLs, and database query concepts do not cross that boundary. A future PostgreSQL, SQL Server, or NoSQL adapter must pass the shared provider contract tests.

The GCP adapter applies a mandatory tenant `meta.security` label, injects `_security` into every search, requests strict handling, verifies the response self-link retained the filter, validates labels after direct reads, and uses tenant-labelled FHIR transactions for atomic writes.

## Configuration

Production requires:

- an external OIDC issuer (`Authentication:Authority`);
- one or more tenant definitions, each with exactly one active 256-bit-or-larger HMAC key;
- a configured durable provider;
- an unpacked `hl7.fhir.r4.core#4.0.1` and `fhir.r4.ukcore.stu2#2.0.2` package directory for the bounded Firely validator pool;
- TLS and client-certificate allow-lists for MLLP.

See [configuration](https://lumbridge.github.io/UnifyEMPI/reference/configuration/),
[matching and blocking rules](https://lumbridge.github.io/UnifyEMPI/matching/rules/),
[architecture](https://lumbridge.github.io/UnifyEMPI/architecture/overview/),
[core paths and processing model](https://lumbridge.github.io/UnifyEMPI/architecture/core-paths/),
and the [Helm values](deploy/helm/unifyempi/values.yaml).

## Safety and scope

No raw inbound FHIR or HL7 bodies are retained by default. Logs contain event types, counts, latency, outcomes, and trace identifiers, not patient values.

This milestone does not include arbitrary clinical resources, runtime assembly loading, live federation, or a second durable database adapter.

Production NHS use still requires an external DPIA, clinical-safety case, penetration test, data-retention policy, and contractual/compliance review. This project supplies technical controls; it does not claim certification.

## Legacy and licence

The original .NET Framework 4.5 prototype is preserved by the `legacy-net45` Git tag. The active solution is `UnifyEMPI.slnx`.

The repository retains its original [CC0 1.0](LICENSE) dedication.

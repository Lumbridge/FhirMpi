---
title: Configuration reference
description: Configure identity, tenants, matching profiles, source trust, storage, portal access and HL7v2 ingress.
---

Environment variables use .NET double-underscore notation, for example `Authentication__Authority`.

Choose the tenant boundary before assigning source systems or generating secrets.
UnifyEMPI never matches across tenants. For the Welsh national registry, each health
board, WDS, Velindre and any additional participating national organisation has a
distinct source-system ID inside one national tenant.
See [identity model and frequently asked questions](/UnifyEMPI/concepts/identity-model/#tenants-and-national-deployment)
for the security and access implications.

## API

```json
{
  "Authentication": {
    "Enabled": true,
    "Authority": "https://identity.example",
    "Audience": "unifyempi",
    "RequireHttpsMetadata": true
  },
  "RegistryProvider": { "Type": "GcpHealthcare" },
  "GcpHealthcare": {
    "StoreName": "projects/PROJECT/locations/europe-west2/datasets/DATASET/fhirStores/STORE"
  },
  "FhirValidation": {
    "UkCorePackageDirectory": "/app/fhir-packages",
    "PoolSize": 8
  },
  "Tenants": {
    "Items": [{
      "TenantId": "nhs-wales",
      "MatchingProfileVersion": "uk-default-v1",
      "PossibleThreshold": 0.62,
      "ProbableThreshold": 0.82,
      "MatchingRules": {
        "Weights": {
          "FamilyName": 0.25,
          "GivenNames": 0.20,
          "BirthDate": 0.30,
          "Address": 0.15,
          "Telecom": 0.07,
          "Gender": 0.03
        },
        "BlockingRules": [
          "AuthoritativeIdentifier",
          "FamilyNameAndBirthDate",
          "PhoneticFamilyNameAndBirthDate",
          "PostcodeAndBirthDate",
          "Phone",
          "Email"
        ],
        "AuthoritativeIdentifierSystems": [
          "https://fhir.nhs.uk/Id/nhs-number"
        ],
        "MaximumCandidates": 500,
        "DefaultResultCount": 10,
        "MaximumResultCount": 50
      },
      "SourceTrust": {
        "aneurin-bevan": 90,
        "betsi-cadwaladr": 90,
        "cardiff-and-vale": 90,
        "cwm-taf-morgannwg": 90,
        "hywel-dda": 90,
        "powys": 90,
        "swansea-bay": 90,
        "wds": 100,
        "velindre": 90
      },
      "AuthoritativeSources": ["wds"],
      "BlockingSecrets": [{
        "Version": "v2",
        "SecretBase64": "<secret reference, never commit>",
        "Active": true
      }, {
        "Version": "v1",
        "SecretBase64": "<previous secret>",
        "Active": false
      }]
    }]
  },
  "Maintenance": {
    "WorkerEnabled": true,
    "PollIntervalSeconds": 5,
    "LeaseSeconds": 60,
    "FhirSources": [],
    "ReconciliationSchedules": []
  }
}
```

The source identifiers mirror the organisation-level Welsh feed model. Add any other
approved national sources using their interface-catalogue identifiers. The trust values
and `AuthoritativeSources` entry above are illustrative: approve them through local data
governance rather than copying them unchanged.

During key rotation, retain the previous key until the durable re-index job completes.
Candidate lookup queries all configured versions; only one version may be active for
new stable IDs and tags. For blocking-rule changes, temporarily deploy the union of the
old and new rules so validation can prove online candidate-discovery continuity.

See [matching and blocking rules](/UnifyEMPI/matching/rules/) for the normative rule
definitions, score formula, configuration constraints, workflow outcomes, examples and
the re-index requirement when blocking inputs change. Deploy the same tenant rule
profile to the API, portal and MLLP hosts. The
[maintenance guide](/UnifyEMPI/guides/maintenance/) documents job APIs, schedules and
external FHIR R4 Patient-source configuration.

## Operations portal

The portal uses a generic OpenID Connect authorisation-code flow with PKCE and a server-side cookie. It does not save access or identity tokens. A production identity must contain exactly one tenant claim and at least one permitted portal scope.

```json
{
  "PortalAuthentication": {
    "Enabled": true,
    "Authority": "https://identity.example",
    "ClientId": "unifyempi-portal",
    "ClientSecret": "<secret reference, never commit>",
    "RequireHttpsMetadata": true,
    "TenantClaimType": "tenant_id",
    "NameClaimType": "name",
    "Scopes": [
      "openid",
      "profile",
      "mpi.review",
      "mpi.audit",
      "mpi.operations",
      "mpi.config.read",
      "mpi.config.write"
    ]
  },
  "Portal": {
    "OverviewLoadTimeoutSeconds": 20,
    "PrerenderInteractiveComponents": false,
    "SeedSyntheticData": false,
    "PublicDemo": false,
    "CircuitRetentionMinutes": 3,
    "DataProtectionKeyPath": "/var/unifyempi/data-protection"
  }
}
```

Supply the same `RegistryProvider`, `GcpHealthcare` and `Tenants` sections used by the API. HMAC material and the OIDC client secret must come from a secret store. `DataProtectionKeyPath` is mandatory outside development when OIDC is enabled and must point to encrypted, durable storage shared by all portal replicas.

`OverviewLoadTimeoutSeconds` bounds the dashboard's provider calls between 5 and 120 seconds. A timeout preserves the interactive circuit, shows a retry action beside the failure, and prevents a transient provider delay from leaving the dashboard in a permanent loading state.

`PrerenderInteractiveComponents=false` keeps the interactive portal from reconciling
server-prerendered overview markup after its Blazor circuit connects. This is the
recommended setting for the operational portal because browser extensions and edge
rewriters can otherwise alter that markup before Blazor takes ownership, terminate the
circuit, and leave a stale loading state visible. Set it to `true` only when the entire
delivery path is known to preserve Blazor's prerendered DOM.

The standard Welsh configuration does not grant `mpi.patient.write`: health-board, WDS,
Velindre and other organisation-owned records remain read-only in the portal. A
deployment may optionally configure `Portal:ManagedSourceSystem` and grant
`mpi.patient.write` for a separate UI-managed record namespace. That source must then be
present in every tenant's `SourceTrust` configuration and should normally be
non-authoritative. It is not a replacement for, or alias of, any Welsh organisation
source.

`PublicDemo=true` is an explicit synthetic-only mode. It requires OIDC to be disabled, displays a permanent warning banner, and permits `SeedSyntheticData=true` in a production environment. Never enable it for a store containing real or potentially identifiable patient information.

Configure the reverse proxy to preserve HTTPS forwarding information, permit Blazor WebSockets, enforce session affinity, and apply an idle timeout longer than the circuit retention interval. The Helm deployment enables forwarded-header processing and `ClientIP` service affinity, but ingress-specific WebSocket and affinity annotations remain the operator's responsibility.

Portal permissions are deliberately separate:

- `mpi.review`: patient workbench, review queue, merge, rejection and split decisions;
- `mpi.patient.write`: create and update records owned by `Portal:ManagedSourceSystem`; also requires `mpi.review` for the portal workflow;
- `mpi.audit`: tenant audit trail;
- `mpi.operations`: operational summary;
- `mpi.config.read`: view tenant source and matching policy;
- `mpi.config.write`: make audited non-secret policy changes; and
- `mpi.admin`: administrative superset.

The interactive session never accepts tenant or source overrides from a route, form or header. The portal derives its managed source from deployment configuration and gives it to the application layer only after checking `mpi.patient.write`. Source-system credentials for external systems belong to the API and MLLP hosts, not to portal users.

## FHIR packages

Use `scripts/Get-FhirPackages.ps1` to fetch and unpack the pinned packages:

```powershell
./scripts/Get-FhirPackages.ps1 -Destination ./artefacts/fhir-packages
```

The runtime never downloads profiles. Production images or volumes must supply the pinned, scanned package files.

## MLLP

Every listener has a fixed tenant and source. For production, configure a server PKCS#12 certificate and an explicit client-certificate thumbprint allow-list. `AllowPlaintext` must remain false.

No MSH value participates in tenant or source authorisation. Sending application, sending facility, message-control ID, and payload digest are used only for deduplication and audit metadata.

`Mllp:MaximumConcurrentConnectionsPerListener` bounds each tenant/source listener independently (default `100`). `Mllp:MaximumMessageBytes` defaults to 2 MiB and may never exceed 16 MiB.

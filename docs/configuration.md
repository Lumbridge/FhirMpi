# Configuration

Environment variables use .NET double-underscore notation, for example `Authentication__Authority`.

Choose the tenant boundary before assigning source systems or generating secrets.
OpenMPI never matches across tenants. For a national registry, participating health
boards and systems normally use distinct source-system IDs inside one national tenant.
See [concepts and frequently asked questions](concepts-and-faq.md#tenants-and-national-deployment)
for the security and access implications.

## API

```json
{
  "Authentication": {
    "Enabled": true,
    "Authority": "https://identity.example",
    "Audience": "openmpi",
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
      "TenantId": "tenant-a",
      "MatchingProfileVersion": "uk-default-v1",
      "SourceTrust": { "pas": 100, "portal": 50 },
      "AuthoritativeSources": ["pas"],
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
  }
}
```

During key rotation, retain the previous key until all canonical records have been re-indexed. Candidate lookup queries all configured versions; only one version may be active for new stable IDs and tags.

## Operations portal

The portal uses a generic OpenID Connect authorisation-code flow with PKCE and a server-side cookie. It does not save access or identity tokens. A production identity must contain exactly one tenant claim and at least one permitted portal scope.

```json
{
  "PortalAuthentication": {
    "Enabled": true,
    "Authority": "https://identity.example",
    "ClientId": "openmpi-portal",
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
      "mpi.patient.write",
      "mpi.config.read",
      "mpi.config.write"
    ]
  },
  "Portal": {
    "OverviewLoadTimeoutSeconds": 20,
    "SeedSyntheticData": false,
    "PublicDemo": false,
    "CircuitRetentionMinutes": 3,
    "DataProtectionKeyPath": "/var/openmpi/data-protection",
    "ManagedSourceSystem": "portal"
  }
}
```

Supply the same `RegistryProvider`, `GcpHealthcare` and `Tenants` sections used by the API. HMAC material and the OIDC client secret must come from a secret store. `DataProtectionKeyPath` is mandatory outside development when OIDC is enabled and must point to encrypted, durable storage shared by all portal replicas.

`OverviewLoadTimeoutSeconds` bounds the dashboard's provider calls between 5 and 120 seconds. A timeout preserves the interactive circuit, shows a retry action beside the failure, and prevents a transient provider delay from leaving the dashboard in a permanent loading state.

`ManagedSourceSystem` is the only source the interactive portal may create or update. It must be present in every tenant's `SourceTrust` configuration and should normally be non-authoritative. The value comes from trusted server configuration and cannot be overridden through a route, form, header or patient resource. Records owned by PAS, maternity, emergency or other external sources remain read-only in the portal.

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

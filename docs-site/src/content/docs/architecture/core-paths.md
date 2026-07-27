---
title: Core paths and processing model
description: Annotated FHIR, HL7v2, matching, merge, split, survivorship and tenant-isolation flows.
---

This document shows how the principal UnifyEMPI paths work as implemented. The diagrams
distinguish synchronous automation, message-driven processing, human-governed actions,
and work that is deliberately not performed in the background.

For definitions of source Patients, canonical Patients, enterprise `Person` links,
tenants, HMAC and blocking tags, see
[identity model and frequently asked questions](/UnifyEMPI/concepts/identity-model/).
For the exact normalisation, blocking, scoring, certainty and routing rules, see the
normative [matching and blocking rules](/UnifyEMPI/matching/rules/).

## Processing model at a glance

| Path | Trigger and timing | Automated? | Changes the registry? |
| --- | --- | --- | --- |
| New source `Patient` | Real-time, inside the FHIR create request | Yes: validation, candidate lookup, scoring, survivorship and routing | Yes |
| Existing source `Patient` update | Real-time, inside the FHIR update request | Yes: validation and refresh of its existing cluster | Yes; it does not currently run a cross-cluster duplicate recheck |
| HL7v2 ADT | Message-driven and synchronous for each MLLP frame | Yes: parse, deduplicate, ingest and match before ACK | Yes |
| FHIR `$match` | Real-time, on demand | Yes: bounded lookup and scoring | No |
| Duplicate workbench search | Real-time, started by an authorised user | Yes: bounded lookup and scoring | No, until the user opens a review |
| Merge or split decision | Real-time, started by an authorised reviewer | Evidence and concurrency checks are automatic; the decision is human | Yes, atomically after the approval threshold is met |
| Overnight or periodic population scan | Not present | No | No |

UnifyEMPI has no internal event bus or overnight matching job in this milestone. FHIR is
request-driven; MLLP is message-driven but the message is processed synchronously before
the sender receives an acknowledgement. Candidate blocking makes those real-time paths
bounded, so they never scan the whole patient population.

## Runtime overview

```mermaid
flowchart LR
    subgraph callers["Trusted entry points"]
        FHIR["Source and consumer systems<br/>FHIR R4 over HTTPS"]
        HL7["Source systems<br/>HL7v2 ADT over MLLP"]
        USER["Authorised registry staff<br/>browser session"]
    end

    subgraph hosts["Independently scalable hosts"]
        API["UnifyEmpi.Api<br/>FHIR, SMART and review APIs"]
        MLLP["UnifyEmpi.Hl7v2.Host<br/>framing, TLS, parsing and ACKs"]
        PORTAL["UnifyEmpi.Portal<br/>Blazor Interactive Server"]
    end

    subgraph core["Shared modular-monolith core"]
        APP["Application workflows<br/>ingest, match, survive, review, merge and split"]
        DOMAIN["Version-neutral domain<br/>no FHIR, HL7, GCP or SQL types"]
        CONTRACT["IIdentityRegistryStore<br/>tenant-bound atomic operations"]
    end

    subgraph providers["Exactly one provider type per host at start-up"]
        MEMORY["In-memory<br/>development and tests"]
        GCP["GCP Healthcare API R4<br/>durable production adapter"]
        FUTURE["PostgreSQL, SQL Server or NoSQL<br/>future adapters using the same contract"]
    end

    FHIR -->|"Synchronous HTTP request"| API
    HL7 -->|"One framed message at a time"| MLLP
    USER -->|"Server-rendered interaction"| PORTAL
    API --> APP
    MLLP --> APP
    PORTAL --> APP
    APP --> DOMAIN
    APP --> CONTRACT
    CONTRACT --> MEMORY
    CONTRACT --> GCP
    CONTRACT -.->|"Not included yet"| FUTURE
```

Each production host points at the same durable registry provider. The API and MLLP
processes can therefore scale independently without splitting the identity decision
logic into distributed services.

## Duplicate detection and matching

```mermaid
flowchart TD
    subgraph triggers["Triggers — no timer and no population sweep"]
        NF["New FHIR source Patient<br/>automatic in the create request"]
        NH["New HL7 source record<br/>automatic in the message path"]
        FM["FHIR Patient/$match<br/>consumer-triggered and read-only"]
        PW["Portal duplicate workbench<br/>user-triggered and read-only"]
        EU["Existing source update<br/>refreshes its linked cluster only"]
    end

    NF --> MODE["Remember trigger mode<br/>ingest or read-only query"]
    NH --> MODE
    FM --> MODE
    PW --> MODE
    EU --> REFRESH["Rebuild its current canonical view and blocking keys<br/>no cross-cluster candidate lookup today"]

    MODE --> NORMALISE["Normalise identity values<br/>Unicode, case, whitespace, phone, email and UK postcode"]
    NORMALISE --> NHS["Validate NHS numbers<br/>canonical system and Modulus 11 check digit"]
    NHS --> RAW["Create available blocking inputs<br/>authoritative ID; family+DOB; phonetic family+DOB;<br/>postcode+DOB; phone; email"]
    RAW --> HMAC["HMAC every input with active and previous tenant key versions<br/>raw demographics are never stored as index tags"]
    HMAC --> LOOKUP["Search the union of blocking tags<br/>maximum 500 canonical candidates; never a table scan"]
    LOOKUP --> BROAD{"More than<br/>500 candidates?"}
    BROAD -->|"Yes"| STOP["Stop safely: stronger identifying data is required<br/>HTTP 400 OperationOutcome; current MLLP path returns AE"]
    BROAD -->|"No"| SCORE["Score every already-normalised candidate<br/>family .25 · given .20 · birth date .30<br/>address .15 · telecom .07 · gender .03"]
    SCORE --> CONFLICT{"Conflicting verified<br/>authoritative ID, or certain ID<br/>with birth-date conflict?"}
    CONFLICT -->|"Yes"| HARD["Hard conflict<br/>never auto-link; a created review is locked to two approvals"]
    CONFLICT -->|"No"| GRADE["Assign grade<br/>certain: verified authoritative exact ID<br/>probable: score ≥ .82<br/>possible: score ≥ .62<br/>none: below .62"]
    HARD --> ROUTE
    GRADE --> ROUTE{"Route using the trigger mode"}
    ROUTE -->|"New ingest + certain"| AUTO["Auto-link source to the existing enterprise identity<br/>inside the same durable mutation"]
    ROUTE -->|"New ingest + probable"| REVIEW["Create a new enterprise identity and review Task<br/>do not merge without governed approval"]
    ROUTE -->|"New ingest + possible or none"| NEW["Create a new enterprise identity<br/>possible matches are not queued by default"]
    ROUTE -->|"$match or workbench"| RETURN["Return ordered candidates, score, grade and evidence<br/>no link, review or patient mutation"]
```

The score is explainable: every result carries field-level comparator, similarity,
weight and contribution. Missing data contributes no evidence; it is never interpreted
as equality. A `certain` grade is not a high numerical score—it requires a verified
tenant-authoritative exact identifier and no hard conflict.

The diagram shows the default `uk-default-v1` profile. Named blocking rounds, field
weights, authoritative identifier systems and bounded limits are configurable per
tenant at deployment; their exact semantics and change-control requirements are defined
in [matching and blocking rules](/UnifyEMPI/matching/rules/).

The current trigger-specific outcomes are:

- a new source record is checked automatically before its durable commit;
- an existing source-local record remains attached to its enterprise identity and is
  not compared with other clusters during that update;
- `$match` and the workbench always run the bounded pipeline on demand but have no
  persistence side effects; and
- a probable ingest match creates a review, while a possible ingest match does not.

This design keeps ordinary ingest latency predictable. A future reconciliation service
could deliberately schedule rechecks, but it must still use bounded blocking-key
partitions rather than a population-wide nested comparison.

## FHIR source create and update

```mermaid
sequenceDiagram
    autonumber
    actor Source as Source system
    participant API as UnifyEmpi.Api
    participant Validator as Bounded R4/UK Core validator pool
    participant Registry as RegistryService
    participant Store as IIdentityRegistryStore

    Source->>API: POST Patient or PUT Patient/{source-resource-id}
    API->>API: Validate JWT, scopes, tenant_id and source_system
    Note over API: Headers, URLs and FHIR extensions cannot override<br/>the trusted tenant or source-system claims.
    API->>API: Parse FHIR JSON or XML
    API->>Validator: Structural and UK Core validation
    Validator-->>API: Validation result

    alt Validation fails
        API-->>Source: 4xx OperationOutcome near the failing request
    else Valid source Patient
        API->>Registry: UpsertPatient(command, idempotency key, expected version)
        Registry->>Store: Check receipt and existing source record
        alt Exact idempotent replay
            Store-->>Registry: Stored outcome
            Registry-->>API: Original accepted result
        else Conflicting replay or stale If-Match
            Registry-->>API: Idempotency or concurrency failure
            API-->>Source: 409/412 OperationOutcome
        else New source record
            Registry->>Store: Bounded blocking-key candidate lookup
            Store-->>Registry: At most 500 canonical candidates
            Registry->>Registry: Score, route, apply deterministic survivorship
            Registry->>Store: Atomic source + canonical + Person + review(s) + audit + receipt
            Store-->>Registry: Durable commit and new versions
            Registry-->>API: Created source Patient
            API-->>Source: 201 + Location + ETag
        else Existing source record
            Note over Registry: Update its linked cluster,<br/>without a cross-cluster duplicate recheck.
            Registry->>Registry: Recalculate survivorship and blocking keys
            Registry->>Store: Atomic source + canonical + Person + audit + receipt
            Store-->>Registry: Durable commit and new versions
            API-->>Source: 200 + new ETag
        end
    end
```

Source credentials can write only their own source profile. Canonical Patients are
server-managed and read-only to consumers.

## FHIR `$match`

```mermaid
sequenceDiagram
    autonumber
    actor Consumer as FHIR consumer
    participant API as UnifyEmpi.Api
    participant Registry as RegistryService
    participant Store as IIdentityRegistryStore
    participant Matcher as Weighted evidence engine

    Consumer->>API: POST Patient/$match with Parameters
    API->>API: Authorise mpi.match and read resource/count/options
    API->>API: Structural + minimum match-input validation
    Note over API: A partial match Patient is not required to satisfy<br/>the complete source-write UK Core profile.
    API->>Registry: Match(profile, onlyCertainMatches, count)
    Registry->>Registry: Normalise and generate versioned HMAC blocking keys
    Registry->>Store: FindCandidates(keys, maximum 500)
    Store-->>Registry: Bounded canonical candidates
    Registry->>Matcher: Score each candidate
    Matcher-->>Registry: Score, grade, evidence and conflict flag
    Registry->>Registry: Filter, sort descending and cap count at 50
    Registry-->>API: MatchResponse
    API-->>Consumer: FHIR searchset Bundle
    Note over Consumer,Store: Every returned entry has a score and match-grade.<br/>No match returns an empty searchset.<br/>This path never changes registry state.
```

## HL7v2 MLLP ingestion

```mermaid
sequenceDiagram
    autonumber
    actor Sender as HL7v2 sender
    participant MLLP as MLLP listener
    participant Parser as nHapi ADT adapter
    participant Registry as RegistryService
    participant Store as IIdentityRegistryStore

    Sender->>MLLP: Mutual-TLS connection and framed ADT message
    MLLP->>MLLP: Bind tenant and source from listener + client certificate
    Note over MLLP: MSH sender fields are message metadata only.<br/>They cannot select a tenant or source.
    MLLP->>Parser: Parse 2.3.1, 2.4 or 2.5.1<br/>A01/A04/A08/A28/A31/A40/A47

    alt Malformed or unsupported
        Parser-->>MLLP: Format or support failure
        MLLP-->>Sender: AR with MSA and ERR details
    else Parsed identity event
        Parser-->>MLLP: PID/MRG identity command + message metadata
        MLLP->>Store: Look up receipt by tenant, bound source,<br/>sending app/facility and control ID
        alt Same control ID and same payload digest
            Store-->>MLLP: Original stored ACK
            MLLP-->>Sender: Replay original ACK
        else Same control ID but different digest
            MLLP-->>Sender: AR — control ID reuse rejected
        else New message
            MLLP->>Registry: Upsert through the same application workflow as FHIR
            opt A40/A47 authoritative identity change
                MLLP->>Registry: Move/merge the previous source identity
            end
            Registry->>Store: Durable registry mutation and receipt
            Store-->>Registry: Commit complete
            MLLP-->>Sender: AA — only after durable completion
        end
    end

    Note over MLLP,Store: If an unexpected processing or provider failure interrupts<br/>the path before AA, the sender receives AE and may retry.
```

Visit-specific content is ignored because the service is an identity registry. Raw HL7
messages are not retained by default; the receipt holds the digest, source metadata and
original acknowledgement required for safe replay.

## Governed duplicate merge

```mermaid
flowchart TD
    CREATE["Review created<br/>automatically from a probable new ingest,<br/>or explicitly from the duplicate workbench"] --> PENDING["Pending<br/>candidate is the intended survivor"]
    PENDING --> CURRENT{"Are both enterprise identities active<br/>and their captured versions still current?"}
    CURRENT -->|"No — another merge or update changed one"| STALE["Portal marks the review superseded<br/>and explains the redirect or version change"]
    STALE --> CLOSE["Authorised reviewer closes outdated work<br/>immutable supersession audit evidence"]
    CURRENT -->|"Yes"| DECIDE{"Authorised reviewer submits<br/>reason + expected review version"}
    DECIDE -->|"Reject"| REJECTED["Rejected<br/>no patient links change"]
    DECIDE -->|"Approve link"| CHECK["Verify reviewer, current enterprise versions,<br/>hard conflicts and effective approval policy"]
    CHECK --> ENOUGH{"Distinct approvals<br/>meet the requirement?"}
    ENOUGH -->|"No"| WAITING["Awaiting second approval<br/>first rationale is immutable evidence"]
    WAITING --> SECOND["A different reviewer submits<br/>a new reason + expected version"]
    SECOND --> CHECK
    ENOUGH -->|"Yes"| ATOMIC["One optimistic-concurrency mutation"]

    ATOMIC --> SURVIVOR["Candidate identity survives<br/>canonical values recalculated by survivorship"]
    ATOMIC --> MOVE["All subject source records move<br/>to the survivor"]
    ATOMIC --> REDIRECT["Subject canonical Patient and Person become inactive<br/>and redirect to the survivor"]
    ATOMIC --> AUDIT["Review completes and immutable AuditEvent evidence is written"]

    REJECTED --> AUDIT_REJECT["Immutable rejection audit evidence"]
```

Ordinary duplicate reviews use the tenant's current one- or two-approval policy. If that
policy is reduced while a review is open, an explicit audited decision can adopt the
current lower threshold; the policy change alone never mutates a patient. Reviews
created from a hard conflict lock their two-approval requirement, as do all split
reviews. A reviewer cannot supply both approvals. If another completed merge replaces
an identity, or a source update changes either canonical identity while a review is
open, its captured evidence becomes obsolete. The portal prevents link and reject
decisions, explains the redirect or version change, and lets the reviewer close the
case as superseded without changing patient links. A fresh duplicate search then
captures current demographics, score, evidence and enterprise versions.

## Source unlink and identity split

```mermaid
flowchart LR
    START["Reviewer opens an active enterprise identity"] --> SELECT["Select some, but not all,<br/>source records to move"]
    SELECT --> CASE["Create split review<br/>reason + expected canonical version"]
    CASE --> LOCK["Approval policy locked to<br/>two distinct reviewers"]
    LOCK --> COMMIT["After final approval:<br/>one atomic optimistic-concurrency mutation"]

    COMMIT --> OLD["Original enterprise identity<br/>rebuilt from remaining sources"]
    COMMIT --> NEW["New UUIDv7 enterprise identity<br/>built from selected sources"]
    COMMIT --> LINKS["Source records and Person links<br/>reassigned to the correct identity"]
    COMMIT --> EVIDENCE["Completed review + immutable audit evidence"]
```

A split is a corrective reassignment, not a hard delete. Both resulting canonical
Patients are deterministically rebuilt from their authoritative source snapshots.

## Canonical materialisation and survivorship

```mermaid
flowchart TB
    S1["Source Patient A<br/>authoritative snapshot + source trust"]
    S2["Source Patient B<br/>authoritative snapshot + source trust"]
    SN["Source Patient N<br/>authoritative snapshot + source trust"]

    S1 --> CHOOSE["Choose the winning value for each field"]
    S2 --> CHOOSE
    SN --> CHOOSE

    CHOOSE --> R1["1. Higher tenant-configured source trust"]
    R1 --> R2["2. Verified value before unverified value"]
    R2 --> R3["3. Newer source update"]
    R3 --> R4["4. Stable source ID tie-breaker<br/>makes repeated runs deterministic"]

    R4 --> CANONICAL["Canonical Patient<br/>read-optimised winning values + useful aliases"]
    R4 --> PERSON["Person<br/>enterprise-to-source links + assurance"]
    R4 --> KEYS["Versioned HMAC blocking tags<br/>for the next bounded lookup"]

    CANONICAL --> HISTORY["Historic names, identifiers, addresses and telecom periods<br/>are preserved when useful; they are not silently discarded"]
```

The same rules run on ingest, approved merge and approved split, so the canonical view
does not depend on process order or reviewer preference.

## Tenant and storage safety boundary

```mermaid
flowchart TB
    JWT["Validated OIDC JWT<br/>tenant_id, optional source_system and scopes"]
    CERT["Configured MLLP listener<br/>tenant, source and allowed client certificate"]
    UNTRUSTED["Untrusted inputs<br/>headers, paths, FHIR extensions and MSH fields"]

    JWT --> CONTEXT["Trusted ActorContext / TenantContext<br/>required by every application and store call"]
    CERT --> CONTEXT
    UNTRUSTED -.->|"Cannot override"| CONTEXT

    CONTEXT --> OP{"Provider operation"}
    OP --> SEARCH["Search<br/>inject mandatory tenant _security filter<br/>and Prefer: handling=strict"]
    SEARCH --> VERIFY_LINK["Verify returned self-link retained<br/>the exact tenant filter"]
    OP --> READ["Read by ID<br/>fetch then verify tenant security label"]
    OP --> WRITE["Atomic write<br/>label every resource and validate<br/>all references are same-tenant"]

    VERIFY_LINK --> RESULT["Return only tenant-bound results"]
    READ --> RESULT
    WRITE --> RESULT

    CONTEXT --> HMAC["Tenant HMAC<br/>non-revealing source IDs and blocking keys"]
    CONTEXT --> LIMIT["Per-tenant rate and concurrency controls<br/>limit noisy neighbours"]
```

The tenant is the hard data-partition and policy boundary. A tenant usually represents
one organisation or data controller. Tenant identity is established before domain code
runs and is carried through every operation; it is not a user-selectable search
parameter.

## Deliberate boundaries

- There is no overnight, periodic or population-wide duplicate scan.
- There is no asynchronous event bus between protocol hosts and the registry core.
- An update to an existing source-local record does not currently initiate a
  cross-cluster duplicate recheck.
- `$match` and duplicate-workbench searches are read-only; only an ingest or approved
  review changes links.
- Live federation to remote source systems is not included; stored source snapshots are
  used for matching and survivorship.
- Runtime provider hot-swapping is not included. A host selects exactly one provider at
  start-up and fails readiness when required capabilities are unavailable.

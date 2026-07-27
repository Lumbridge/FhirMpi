---
title: NHS Wales source model
description: How UnifyEMPI represents Welsh health boards, WDS, Velindre and additional national organisations.
---

The reference Welsh deployment uses one national tenant and a distinct source-system
identity for each participating organisation. A source identifies who owns and governs
a Patient feed; it does not describe the application type that happened to send it.

## Reference sources

| Organisation | Source-system ID |
| --- | --- |
| Aneurin Bevan University Health Board | `aneurin-bevan` |
| Betsi Cadwaladr University Health Board | `betsi-cadwaladr` |
| Cardiff and Vale University Health Board | `cardiff-and-vale` |
| Cwm Taf Morgannwg University Health Board | `cwm-taf-morgannwg` |
| Hywel Dda University Health Board | `hywel-dda` |
| Powys Teaching Health Board | `powys` |
| Swansea Bay University Health Board | `swansea-bay` |
| Welsh Demographic Service | `wds` |
| Velindre University NHS Trust | `velindre` |

The seven health-board names follow the current
[NHS Wales organisation list](https://www.nhs.wales/about-us/). WDS is the national
demographic service used to support NHS-number and demographic data flows; Velindre is
kept as its own organisation-owned source.

## Tenant boundary

```text
Tenant: nhs-wales
  Source: aneurin-bevan
  Source: betsi-cadwaladr
  Source: cardiff-and-vale
  Source: cwm-taf-morgannwg
  Source: hywel-dda
  Source: powys
  Source: swansea-bay
  Source: wds
  Source: velindre
```

This allows cross-organisation candidate discovery inside the governed national
registry while keeping provenance, permissions, trust and audit evidence source-bound.
UnifyEMPI never matches across tenants.

## What is not a source

Programme, portal and application categories are not used as reference national
sources. If one organisation legitimately operates multiple independently governed
feeds, give each feed an agreed identifier under that organisation's governance rather
than inventing a national application category.

## Authority and trust

The sample configuration marks `wds` authoritative and gives it the highest source
trust. That is an illustrative starting point, not an automatic governance decision.
Production values must be agreed through local data-quality, information-governance and
clinical-safety processes.

Health-board, WDS, Velindre and other organisation-owned records remain read-only in
the operations portal. A deployment may separately add a UI-managed source for
governed data entry; the public demo calls this source `demo-ui`.

See [configuration](/UnifyEMPI/reference/configuration/) for the complete example and
[identity concepts](/UnifyEMPI/concepts/identity-model/) for the distinction between source and
canonical Patients.

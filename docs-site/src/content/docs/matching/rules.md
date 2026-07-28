---
title: Matching and blocking rules
description: Normative candidate discovery, normalisation, scoring, certainty, conflicts and workflow routing.
---

This document is the normative description of UnifyEMPI's current patient-candidate
discovery and pairwise matching behaviour. It describes `uk-default-v1` as implemented;
if this document and the code disagree, treat that as a defect.

The design follows the same explicit **standardise → block → score → resolve** separation
used by mature identity systems. HAPI FHIR MDM describes candidate searches as an OR
union of configurable blocking searches followed by separately configured match fields.
OpenEMPI similarly separates standardisation, blocking, scoring and resolution and
emphasises steward review. UnifyEMPI adopts those useful boundaries while retaining a
small, bounded set of patient-specific rules:

- [HAPI FHIR MDM rules](https://hapifhir.io/hapi-fhir/docs/server_jpa_mdm/mdm_rules.html)
- [OpenEMPI Entity Edition documentation](https://openempi.atlassian.net/wiki/spaces/openempi30/overview)
- [OpenEMPI product overview](https://www.openempi.org/)
- [OpenEMPI release notes](https://openempi.atlassian.net/wiki/spaces/openempi30/pages/1294008321/Release%2BNotes)

UnifyEMPI supports deterministic candidate blocking followed by either the default
explainable weighted similarity score or a versioned Fellegi–Sunter model calibrated
from governed labels. It does not claim adaptive machine learning, arbitrary FHIRPath
match expressions or runtime-loaded comparator plugins. Explicit trusted-identifier
and hard-conflict rules remain outside either demographic scorer.

## Decision pipeline

For a new source record, FHIR `$match`, or duplicate-workbench search, UnifyEMPI:

1. establishes the trusted tenant and, for ingestion, the trusted source system;
2. normalises the supplied identity fields;
3. creates every configured blocking key for which the required fields are present;
4. HMACs those keys with every configured tenant key version;
5. retrieves the union of active canonical patients sharing at least one key;
6. stops if the candidate set exceeds the configured safety limit;
7. scores every candidate using the configured weighted or Fellegi–Sunter profile;
8. applies authoritative-identifier certainty and hard-conflict rules;
9. assigns a grade; and
10. routes the result according to the calling workflow.

Blocking answers **which candidates may be compared**. Scoring answers **how similar
each retrieved candidate is**. A patient that shares no enabled blocking key is not
scored, even if the pair would otherwise have a high score.

UnifyEMPI never performs a population-wide fallback scan.

## Tenant rule configuration

All three hosts that can execute registry workflows—the API, portal and HL7v2 MLLP
host—must receive the same tenant configuration. The following is the complete default
rule profile expressed explicitly:

```json
{
  "TenantId": "tenant-a",
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
    "Comparators": {
      "Version": "comparators-v1",
      "FamilyName": ["JaroWinkler", "Phonetic"],
      "GivenNames": ["JaroWinkler"],
      "PhoneticMatchFloor": 0.85,
      "NicknameMatchFloor": 0.92,
      "NicknameDictionaries": []
    },
    "MaximumCandidates": 500,
    "DefaultResultCount": 10,
    "MaximumResultCount": 50
  }
}
```

Configuration is validated at host start-up:

- thresholds must be finite values from `0` to `1`, with possible below probable;
- each weight must be finite and from `0` to `1`, and at least one must be positive;
- at least one recognised blocking rule must be enabled, with no duplicates;
- authoritative identifier systems must be unique absolute URIs;
- comparator names must be recognised and unique, similarity floors must be in
  `(0,1]`, and nickname dictionaries must be versioned and unambiguous;
- an optional Fellegi–Sunter model must define every comparison level for all six
  fields, use positive m/u probabilities whose per-field distributions sum to one,
  and declare a production prior strictly between zero and one;
- `MaximumCandidates` must be from `1` to the provider safety limit of `500`;
- `MaximumResultCount` must be from `1` to `100` and no greater than
  `MaximumCandidates`; and
- `DefaultResultCount` must be from `1` to `MaximumResultCount`.

The weights are relative. They do not have to total `1`; the scorer divides by their
configured total.

`MatchingProfileVersion` is recorded with match and review evidence. Change it whenever
a weight, threshold, blocking rule, identifier system, comparator implementation or
normalisation rule changes. The version is an audit label, not a dynamic algorithm
selector.

Changing blocking rules or identifier systems on a populated registry requires every
canonical patient to be re-indexed. Deploy the union of old and new rules, complete the
guarded online re-index, then remove obsolete inputs. Merely changing configuration can
otherwise reduce candidate recall because existing records do not contain newly added
keys. Comparator or calibrated-probability changes do not alter blocking keys, but
should be followed by population reconciliation to discover changed review candidates.

## Normalisation rules

Normalisation runs before both blocking and scoring.

| Field | Implemented normalisation |
| --- | --- |
| Identifier system | Trim surrounding whitespace; comparisons are ordinal and case-sensitive. |
| NHS number value | Retain digits only. Validity is a separate 10-digit Modulus 11 check. |
| Other identifier value | Trim surrounding whitespace; preserve case and internal characters. |
| Family and given names | Unicode Form D; remove combining marks; uppercase letters and digits; replace every run of other characters with one space; trim. |
| Family-name phonetic value | Apply UnifyEMPI's bounded Metaphone-style encoder to letters from the normalised family name; retain at most eight encoded characters. |
| Address text | Normalise and concatenate address lines, city and district. Country is not part of address scoring. |
| Postcode | Retain uppercase letters and digits only. |
| Postcode sector | For a normalised postcode of at least five characters, remove the final two characters; otherwise no sector is produced. |
| Email | Trim and uppercase. |
| Phone, SMS, fax and pager | Retain digits; preserve a leading `+` only when the trimmed input started with `+`. No country-code inference is performed. |
| Birth date | Preserve the parsed calendar date. |
| Gender | Preserve the parsed administrative-gender enum. |

Blank identifiers, empty names, empty addresses and empty telecom values are discarded
from the normalised identity.

## Blocking rules

Enabled rules are independent blocking rounds. The candidate set is the deduplicated
**OR union** of all rounds. Fields inside one composite round are **AND conditions**.
For example, `FamilyNameAndBirthDate` requires both a family name and a birth date.
If either is missing, that round is skipped; the remaining available rounds still run.

| Rule name | Required normalised input | Internal raw shape |
| --- | --- | --- |
| `AuthoritativeIdentifier` | Identifier whose system appears in `AuthoritativeIdentifierSystems` | `ID\|{system}\|{value}` |
| `FamilyNameAndBirthDate` | Each non-empty family name plus birth date | `FMDOB\|{family}\|{yyyyMMdd}` |
| `PhoneticFamilyNameAndBirthDate` | Each non-empty family phonetic code plus birth date | `PHDOB\|{phonetic}\|{yyyyMMdd}` |
| `PostcodeAndBirthDate` | Each non-empty full postcode plus birth date | `PCDOB\|{postcode}\|{yyyyMMdd}` |
| `Phone` | Each phone or SMS value | `TEL\|{system}\|{value}` |
| `Email` | Each email value | `TEL\|Email\|{value}` |

The raw shapes exist only in memory. For every raw key and every configured tenant
secret version, UnifyEMPI creates a `BlockingKey` whose string representation is:

```text
{secret-version}:{uppercase-hex(HMAC-SHA-256(secret, UTF-8(raw-key)))}
```

Each secret must contain at least 256 bits and exactly one configured version must be
active. Candidate queries include active and retained previous versions so a rotation
can overlap safely. Retain the previous secret until all canonical records have been
written with the new version.

Important identifier distinction:

- membership in `AuthoritativeIdentifierSystems` is sufficient to create an identifier
  blocking key;
- an identifier must additionally be valid, verified and authoritative on both records
  to create a `Certain` result or a hard identifier conflict; and
- consequently, an invalid NHS number may help retrieve a candidate but can never make
  the pair certain.

If no enabled rule can produce a key, the request is rejected for insufficient identity
data. At the default profile, a request therefore needs at least one configured
identifier, family name plus birth date, postcode plus birth date, phone/SMS or email.

Only active canonical patients are candidates. The configured default cap is `500`. If
the provider reports that more candidates share the supplied keys, UnifyEMPI rejects the
operation instead of scoring a partial, arbitrary subset. The caller must provide
stronger identifying data or the operator must tune overly broad blocking rounds.

## Numeric matching rules

Every retrieved candidate receives six field similarities. For each field:

```text
contribution(field) = similarity(field) × weight(field)

score = clamp(
  sum(contribution(field)) / sum(configured weights),
  0,
  1)
```

With the default weighted scorer, missing data has similarity `0` and the denominator
is **not** reduced or reweighted. This deliberately makes a sparse record less able to
cross a demographic threshold. In a Fellegi–Sunter profile, missing comparisons are
neutral and add no likelihood ratio. Identifiers do not contribute to either
demographic score: they control certainty and hard conflicts separately.

### Default field comparators and weights

| Field | Weight | Similarity rule |
| --- | ---: | --- |
| Family name | `0.25` | Compare every non-empty family-name pair with Jaro–Winkler and take the highest value. If both phonetic codes are equal and non-empty, the pair receives at least `0.85`. |
| Given names | `0.20` | Deduplicate all normalised given names, compare every cross-record pair with Jaro–Winkler and take the highest value. Name position and order do not contribute. |
| Birth date | `0.30` | Exact date: `1`; same year with day and month transposed: `0.5`; otherwise `0`. |
| Address | `0.15` | For every address pair, calculate full-postcode exact=`1`, postcode-sector exact=`0.6`, otherwise `0`; also calculate token-set Jaccard similarity over lines/city/district. Use the greater value, then take the best address pair. |
| Telecom | `0.07` | `1` when any normalised telecom has the same system and exact value on both records; otherwise `0`. |
| Gender | `0.03` | `1` for equal, known administrative gender; unknown or unequal is `0`. |

Family and given-name profiles may additionally select `Exact`,
`NormalisedDamerauLevenshtein`, `DiceCoefficient`, `Phonetic`, and `Nickname`.
Every pair is evaluated with the configured catalogue and the highest comparator
result is retained. Nickname groups are tenant-supplied, culture-labelled and
versioned; there is no built-in nickname list.

The evidence returned with each match includes the field name, winning comparator,
similarity, configured weight, comparison level and contribution. Under
Fellegi–Sunter it also includes the field log likelihood ratio. `ScoreMethod` states
whether `Score` is `weighted-similarity` or a calibrated `fellegi-sunter` probability.

### Identifier certainty and conflicts

For every configured authoritative identifier system, UnifyEMPI constructs the set of
values that are:

- present on the record;
- marked verified;
- marked authoritative; and
- valid according to the system-specific validator. NHS numbers must pass the 10-digit
  Modulus 11 check.

The result is then determined as follows:

1. If both records have trusted values for a system and the sets overlap, that system
   supplies a certain identifier.
2. If both have trusted values for a system and the sets do not overlap, the pair has an
   authoritative-identifier hard conflict.
3. A certain identifier combined with different, present birth dates is also a hard
   conflict.
4. Any hard conflict prevents the `Certain` grade, even if another identifier system
   agrees.
5. A certain identifier with no hard conflict produces `Certain` regardless of the
   numeric demographic score.
6. Without `Certain`, or when a hard conflict exists, the numeric thresholds determine
   the grade.

On source ingestion, an identifier is marked verified and authoritative only when its
source is listed in the tenant's `AuthoritativeSources`, its system is configured, and
its value passes validation. For `$match`, a valid supplied identifier in a configured
system is treated as authoritative for the query side; the stored candidate must still
contain a trusted value.

`SourceTrust` does not change match weights or scores. It controls canonical-value
survivorship after records are linked.

## Grades

Thresholds are inclusive. With the default profile they apply to weighted similarity;
with an activated Fellegi–Sunter profile they apply to calibrated probability:

| Condition | Grade |
| --- | --- |
| Certain identifier and no hard conflict | `Certain` |
| Otherwise, score `>= 0.82` | `Probable` |
| Otherwise, score `>= 0.62` | `Possible` |
| Otherwise | `None` |

A hard conflict does not force the numeric grade to `None`; it prevents only
`Certain`. A demographically strong but conflicting pair can therefore be `Probable`
with `HasHardConflict=true`, allowing it to reach governed review without being
auto-linked.

## Workflow-specific outcomes

| Workflow | Candidate and result handling |
| --- | --- |
| New source record | Score every bounded candidate and discard `None`. Select the highest-scoring non-conflicting `Certain` candidate and auto-link. If there is no certain candidate, create a new enterprise identity and one review per `Probable` candidate. `Possible` candidates are not queued automatically. |
| Existing source-record update | Keep the source attached to its current enterprise identity and rebuild that cluster. No cross-cluster candidate search runs today. |
| FHIR `$match` | Return non-`None` results ordered by descending score, then enterprise ID. `onlyCertainMatches=true` retains only `Certain`. A missing or non-positive count uses the configured default; the result is capped at the configured maximum. No registry state changes. |
| Duplicate workbench | Exclude the subject identity, return non-`None` results ordered by descending score then enterprise ID, and cap the result count. No state changes until a reviewer creates a case. |
| Manual duplicate review | Re-score the explicitly selected pair even if it was not found through blocking, record the current evidence and versions, and require governed approval. |

A review created with `HasHardConflict=true` is locked to two distinct approvals.
Ordinary probable reviews use the tenant's configured one- or two-approval policy.

## Worked examples with default weights

### Sparse exact demographics

Exact family name, given name and birth date contribute:

```text
0.25 + 0.20 + 0.30 = 0.75
```

With no trusted identifier and no other evidence, the result is `Possible`, not
`Probable`.

### Exact NHS number with sparse demographics

Two records share the same valid, verified and authoritative NHS number. Even if all
six demographic similarities are zero, the result is `Certain` because identifier
certainty is separate from the numeric score.

### Exact NHS number with a conflicting birth date

The shared trusted NHS number would normally be certain, but two present and different
birth dates create a hard conflict. The pair cannot be `Certain`; its grade falls back
to the numeric thresholds. It is never auto-linked and, if a review is created, that
review requires two distinct approvals.

### Broad blocking input

A common family name plus birth date may retrieve more than 500 candidates. UnifyEMPI
does not score the first 500 and pretend the result is complete. It rejects the request
and asks for stronger input such as a configured identifier, postcode, phone or email.

## Tuning and change control

Treat rule changes as clinical-safety and data-quality changes, not routine UI styling:

1. prepare a labelled, synthetic or properly governed ground-truth dataset;
2. measure blocking recall before scoring precision—missed candidates cannot be rescued
   by a better comparator;
3. inspect candidate-set size and the `unifyempi.match.candidates` metric for excessively
   broad rounds;
4. measure false-link and missed-link rates separately at the possible and probable
   thresholds;
5. verify performance at realistic tenant scale and data sparsity;
6. assign a new matching-profile version;
7. re-index canonical patients when blocking inputs or HMAC versions change; and
8. deploy the identical profile to API, portal and MLLP hosts.

Do not copy another product's default thresholds blindly. HAPI FHIR and OpenEMPI both
make matching configurable because useful rules depend on local data quality, identifier
governance and review capacity.

Use the [matching assurance and calibration guide](/UnifyEMPI/guides/matching-assurance/)
to submit tenant-bound labels, measure blocking recall and precision/recall with
confidence intervals, govern nickname dictionaries, and produce a held-out
Fellegi–Sunter calibration report.

## Feature alignment and deliberate gaps

| Capability | UnifyEMPI status |
| --- | --- |
| Configurable OR blocking rounds with composite AND fields | Implemented through the six named, bounded rules above. |
| Configurable field weights, thresholds, identifier systems and safety limits | Implemented through tenant deployment configuration. |
| Normalise, block, score, resolve pipeline | Implemented and explained in field-level evidence. |
| Deterministic trusted-identifier match and conflict overrides | Implemented. |
| Human review with merge/reject, two-person conflict policy and audit trail | Implemented. |
| Canonical/golden patient survivorship | Implemented with source trust, verification, recency and deterministic tie-breaking. |
| Batch re-index and scheduled population reconciliation | Implemented as durable, leased, resumable jobs with staged-overlap validation, governed duplicate reviews and optional incremental FHIR R4 Patient ingestion. |
| Ground-truth blocking and matching evaluation reports | Implemented for tenant-bound labelled source-record pairs, including confidence intervals, field diagnostics and bounded error examples. |
| Nickname/synonym dictionaries and additional phonetic/comparator algorithms | Implemented through versioned, culture-labelled tenant dictionaries and the exact, Jaro–Winkler, normalised Damerau–Levenshtein, Dice, phonetic and nickname catalogue. |
| Fellegi–Sunter probability calibration or null-value probability models | Implemented with stratified held-out validation, additive smoothing, explicit production prior, m/u reporting, neutral missing values and governed configuration activation. |
| Trainable/adaptive ML classification | Not implemented and should not be introduced without governed training data, validation, drift monitoring and human override. |
| Arbitrary domain/entity models | Not implemented; UnifyEMPI is deliberately patient-focused. |
| Webhooks and broad non-healthcare integration adapters | Not implemented. Current interfaces are FHIR R4, HL7v2 MLLP and reviewer APIs. |

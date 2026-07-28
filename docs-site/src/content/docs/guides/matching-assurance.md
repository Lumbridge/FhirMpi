---
title: Matching assurance and calibration
description: Evaluate blocking recall and match precision, govern nickname dictionaries, and calibrate Fellegi–Sunter probabilities from labelled source-record pairs.
---

UnifyEMPI can evaluate a tenant's active matching profile against governed clerical
labels and can derive an optional Fellegi–Sunter probability model. These operations
are administrative assurance tools. They do not merge patients, change source records,
or silently activate a trained model.

## Operations portal workbench

An authorised administrator can run the same operations from **08 Match assurance** in
the operations portal. Paste tab-separated labels using these five columns:

```text
leftSource	leftLocalId	rightSource	rightLocalId	isMatch
health-board-a	record-100	wds	record-900	match
health-board-a	record-101	wds	record-901	non-match
```

The header is optional; accepted outcomes are `match`/`non-match`, `true`/`false` or
`1`/`0`. The workbench shows the active matching, comparator and score-method versions
before execution. Evaluation displays threshold metrics, field diagnostics and bounded
error references. Calibration displays held-out quality, candidate thresholds and the
complete model JSON for governed configuration change. It never provides an activation
button.

The signed-in identity must carry `mpi.admin`, and the portal OIDC client must request
and be allowed that scope. Do not add `mpi.admin` merely to expose the page to ordinary
reviewers; use the API from a separately controlled assurance process when local
separation of duties requires it.

## Ground-truth contract

Labels refer to source records already present in the tenant registry. The request does
not upload another copy of patient demographics:

```json
{
  "datasetId": "clerical-review-2026-q3",
  "pairs": [{
    "left": {
      "sourceSystem": "health-board-a",
      "localId": "record-100"
    },
    "right": {
      "sourceSystem": "wds",
      "localId": "record-900"
    },
    "isMatch": true
  }, {
    "left": {
      "sourceSystem": "health-board-a",
      "localId": "record-101"
    },
    "right": {
      "sourceSystem": "wds",
      "localId": "record-901"
    },
    "isMatch": false
  }],
  "thresholds": [0.62, 0.82, 0.95],
  "maximumErrorExamples": 25
}
```

Submit it to:

```text
POST /api/v1/matching/evaluation
Scope: mpi.admin
```

The service rejects missing records, self-pairs, duplicate or contradictory pair
labels, cross-tenant access, control characters, datasets without both classes, and
requests above 10,000 pairs.

Use labels produced through an approved clerical-review protocol. A convenient or
artificially balanced sample is useful for development, but precision and recall
estimates are representative only when the labelled sample represents the population
and decision boundary being reported. Keep the dataset version, sampling frame,
label definitions, adjudication process, and reviewer agreement with the governance
record.

## Evaluation report

The report includes:

- the active matching, comparator, and score-method versions;
- a SHA-256 digest of the ordered pair references and labels;
- label and class counts;
- blocking recall, treating a true pair with insufficient or non-overlapping blocking
  keys as a missed candidate;
- true/false positive and negative counts at each threshold;
- precision, recall, specificity, negative predictive value, F1 and Matthews
  correlation coefficient;
- Wilson 95% intervals for precision and recall;
- mean observed similarity by field and class; and
- up to 100 high-confidence false-positive or false-negative examples for authorised
  investigation.

Missing metric denominators are returned as `null`, never as a misleading zero. The
audit trail records only the dataset ID, digest, class count and action; it does not
copy the labelled record identifiers into audit text.

## Comparator profiles and nickname dictionaries

Name comparators are configured per tenant and versioned with the matching profile:

```json
{
  "Comparators": {
    "Version": "welsh-names-2026-q3",
    "FamilyName": [
      "JaroWinkler",
      "NormalisedDamerauLevenshtein",
      "Phonetic"
    ],
    "GivenNames": [
      "JaroWinkler",
      "NormalisedDamerauLevenshtein",
      "DiceCoefficient",
      "Nickname"
    ],
    "PhoneticMatchFloor": 0.85,
    "NicknameMatchFloor": 0.92,
    "NicknameDictionaries": [{
      "Version": "en-gb-clinically-reviewed-v1",
      "Culture": "en-GB",
      "Entries": {
        "Robert": ["Bob", "Rob"],
        "William": ["Bill", "Will"]
      }
    }]
  }
}
```

The supported catalogue is `Exact`, `JaroWinkler`,
`NormalisedDamerauLevenshtein`, `DiceCoefficient`, `Phonetic`, and `Nickname`.
UnifyEMPI evaluates the configured comparators and records the one that supplied the
highest similarity in field evidence. Nickname evidence also records the culture and
dictionary version.

No nickname list is enabled by default. Names, transliterations, diminutives and
aliases are language- and population-dependent; deploying organisations must review
the content with data owners and equality specialists. The configuration validator
rejects an alias assigned to multiple groups anywhere in one comparator profile, duplicate versions,
empty values, excessive dictionaries, unknown comparators, and a `Nickname`
comparator without a dictionary.

## Fellegi–Sunter calibration

Submit at least ten matches and ten non-matches:

```json
{
  "datasetId": "clerical-review-2026-q3",
  "modelVersion": "fs-2026-q3-v1",
  "pairs": [],
  "priorMatchProbability": 0.0005,
  "smoothing": 1.0,
  "validationFraction": 0.2,
  "targetPrecision": 0.99
}
```

```text
POST /api/v1/matching/calibration/fellegi-sunter
Scope: mpi.admin
```

Supply the complete `pairs` collection shown in the evaluation contract. The prior is
the probability that a pair in the **production comparison population** is a true
match. It is explicit because a clerical sample is often deliberately balanced and
must not be used to infer production prevalence.

Calibration:

1. deterministically and separately splits each label class into training and
   validation sets;
2. assigns each observed field comparison to `Exact` (`>=0.999999`), `Strong`
   (`>=0.85`), `Partial` (`>=0.5`), or `Different`;
3. estimates the per-field `m` and `u` distributions from the training labels with
   configurable additive smoothing;
4. leaves missing comparisons neutral rather than treating absent data as
   disagreement;
5. combines the explicit prior odds and per-field log likelihood ratios;
6. reports held-out Brier score, log loss, threshold metrics, an F1 threshold, and the
   lowest validation threshold meeting the requested precision.

The standard conditional-independence limitation of Fellegi–Sunter still applies.
Correlated fields can overstate evidence, so inspect m/u values, held-out errors,
subgroup performance and drift before activation.

## Activation and rollback

Calibration returns a complete `model` object. It is deliberately **not activated** by
the endpoint. To use it:

1. approve the dataset, sampling frame, model parameters and threshold trade-off;
2. copy the returned `model` object to the tenant's
   `MatchingRules:FellegiSunter` configuration;
3. assign a new `MatchingProfileVersion`;
4. deploy the identical profile to the API, portal and MLLP hosts;
5. run ground-truth evaluation again against an independent holdout dataset; and
6. monitor review volumes, errors and population drift.

With a model configured, match `Score` is the calibrated probability and
`ScoreMethod` is `fellegi-sunter`. Field evidence includes each log likelihood
contribution. Trusted-identifier certainty and hard-conflict rules remain outside the
probability model and retain their safety precedence.

Rollback by restoring the previous tenant profile and version. Comparator-only changes
do not require re-indexing. A comparator or probability change should still run
population reconciliation because historical probable-match queues may change.
Blocking-rule changes require the staged online
[re-index process](/UnifyEMPI/guides/maintenance/).

## Operating principles

- Never train or evaluate across tenants.
- Never treat a high apparent accuracy on an imbalanced dataset as sufficient; report
  precision and recall separately.
- Never infer the production prior from a case-control clerical sample.
- Never activate a calibration result automatically.
- Keep demographic comparator evidence reviewable; deterministic verified identifiers
  and hard conflicts remain explicit.
- Re-evaluate after source, normalisation, nickname, comparator, threshold, blocking,
  or population changes.

The metric definitions and m/u formulation follow the same established linkage model
described by the
[Splink edge-metrics guide](https://moj-analytical-services.github.io/splink/topic_guides/evaluation/edge_metrics.html)
and
[Fellegi–Sunter guide](https://moj-analytical-services.github.io/splink/topic_guides/theory/fellegi_sunter.html).

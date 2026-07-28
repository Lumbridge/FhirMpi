---
title: "ADR 0003: Governed matching assurance and calibration"
description: Why evaluation uses tenant record references and supervised held-out calibration never activates a model automatically.
---

**Status:** Accepted

## Context

An explainable score is not evidence that blocking retrieves true pairs or that
decision thresholds achieve acceptable precision and recall in the deploying
population. Nicknames and comparator behaviour are population-dependent. A
Fellegi–Sunter model also needs defensible `m` and `u` probabilities, a production
match prior and validation outside the fitting sample.

Assurance data is sensitive: duplicating demographics in an evaluation request or
retaining clerical labels indefinitely would create another patient dataset. Automatic
training or activation could silently change identity-linking behaviour.

## Decision

UnifyEMPI provides admin-only, tenant-bound evaluation and Fellegi–Sunter calibration
through the API and operations portal.

- Labels contain references to source records already in the tenant registry and a
  match/non-match outcome. They do not contain copied demographics.
- Requests reject missing records, self-pairs, duplicate or contradictory labels,
  single-class datasets and more than 10,000 pairs.
- Evaluation applies the active blocking, comparator and scoring profile and reports
  blocking recall, threshold confusion matrices, precision and recall with Wilson
  intervals, specificity, negative predictive value, F1, Matthews correlation,
  per-field discrimination and bounded error references.
- Audit evidence stores the dataset ID, ordered-label digest and aggregate counts, not
  the record-pair identifiers.
- Comparator selection and nickname dictionaries are versioned tenant configuration.
  Dictionaries are culture-labelled, ambiguity is rejected, and no nickname content is
  enabled by default.
- Calibration estimates discrete per-field `m` and `u` distributions from governed
  labels with additive smoothing. It uses an explicit production prior, treats missing
  comparisons as neutral and evaluates on a deterministic class-stratified holdout.
- The report includes Brier score, log loss, threshold metrics and candidate operating
  points. It returns a versioned model but never writes it into active configuration.
- Trusted-identifier certainty and hard conflicts remain deterministic controls outside
  the probability model.

Activation requires human approval of the sampling frame, labels, prior, diagnostics
and trade-off; insertion into a new matching-profile version; consistent deployment to
all hosts; and evaluation on an independent holdout. Rollback restores the previous
profile. Comparator or scoring changes should trigger population reconciliation;
blocking-input changes additionally require online re-indexing.

## Consequences

- Operators can quantify both candidate-generation recall and classification quality
  without uploading a duplicate demographic dataset.
- Model lineage is explicit in dataset, comparator, matching-profile and model versions.
- Balanced clerical samples remain useful for fitting but cannot silently establish the
  real-world prior or production precision.
- Deploying organisations own label quality, subgroup analysis, drift monitoring,
  equality review and clinical acceptance.
- Calibration remains an offline governed action; UnifyEMPI does not adapt from live
  decisions.

## Rejected alternatives

- **Persist uploaded demographic training rows.** Record references keep identity data
  in the registry's existing tenant and retention boundary.
- **Use unsupervised EM or online learning by default.** These approaches make
  population assumptions and behavioural change harder to approve and reproduce.
- **Infer the production prior from a case-control clerical sample.** Deliberately
  balanced samples do not represent comparison-pair prevalence.
- **Ship a universal nickname dictionary.** Diminutives, aliases and transliterations
  vary by language and population and require local governance.
- **Activate the fitted model or thresholds automatically.** Evaluation evidence,
  independent validation and explicit release control must precede behaviour change.

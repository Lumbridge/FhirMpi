---
title: Performance gates
description: Run and interpret the scoring benchmark, regression gate and end-to-end k6 load scenario.
---

UnifyEMPI has two runnable performance tools and one larger release-acceptance target.
They measure different boundaries:

| Level | Boundary | Gate or target |
| --- | --- | --- |
| BenchmarkDotNet | One in-process comparison against 500 already-normalised candidates; no HTTP, authentication, network or storage | Checked-in CI mean, no more than 10% regression, and a hard 10 ms mean ceiling |
| k6 `$match` | HTTP, authentication, candidate lookup, scoring, serialisation and the selected provider | 250 requests/second for 30 minutes, p95 below 250 ms and fewer than 0.1% failed requests |
| Registry scale | Candidate blocking and matching against a representative durable registry | Release-acceptance target of 10 million canonical identities |

Do not present the core benchmark mean as an end-to-end latency percentile or
throughput result. The 10-million-identity target also requires a separately
provisioned population; there is no synthetic population generator for it in this
repository.

## Core scoring benchmark

Run the benchmark from the repository root in Release mode:

```powershell
dotnet restore benchmarks/UnifyEmpi.Benchmarks/UnifyEmpi.Benchmarks.csproj --locked-mode
dotnet run --project benchmarks/UnifyEmpi.Benchmarks -c Release --no-restore -- `
  --filter "*ScoreFiveHundred*" --job short --exporters json
```

BenchmarkDotNet writes its reports beneath `BenchmarkDotNet.Artifacts/results`. The
regression script reads the newest full JSON report:

```powershell
./scripts/Test-BenchmarkRegression.ps1
```

`benchmarks/baseline.json` identifies the method, expected mean, hard ceiling, allowed
regression and measurement environment. The baseline is for the hosted Ubuntu CI
runner. A slower or busy workstation can fail the comparison without proving a product
regression, so:

- close high-load applications and repeat a local run before investigating noise;
- compare like-for-like CI runs when deciding whether a regression is real; and
- never update the baseline only to make a pull request pass.

Re-baseline only when an intentional algorithm, benchmark-methodology, runtime or CI
runner change makes the old value invalid. Record the reason and the measurement
environment in the same change.

## End-to-end k6 scenario

`benchmarks/k6/match.js` sends a synthetic FHIR R4 `$match` request. A short local run
is useful for verifying connectivity and script configuration:

```powershell
$env:BASE_URL = "http://localhost:8080"
$env:DURATION = "30s"
$env:RATE = "5"
$env:PREALLOCATED_VUS = "10"
$env:MAX_VUS = "50"
k6 run benchmarks/k6/match.js
```

This smoke run does not establish performance: the in-memory development API has no
representative population or network boundary.

For an acceptance run, pre-seed an isolated durable store with synthetic data at the
intended scale, run k6 in the same region, and use the default workload:

```powershell
$env:BASE_URL = "https://unifyempi-test.example"
$env:ACCESS_TOKEN = "<short-lived system token>"
Remove-Item Env:DURATION -ErrorAction SilentlyContinue
Remove-Item Env:RATE -ErrorAction SilentlyContinue
Remove-Item Env:PREALLOCATED_VUS -ErrorAction SilentlyContinue
Remove-Item Env:MAX_VUS -ErrorAction SilentlyContinue
k6 run benchmarks/k6/match.js
```

The defaults are:

| Variable | Default | Meaning |
| --- | --- | --- |
| `BASE_URL` | `http://localhost:8080` | API origin, without a trailing path |
| `ACCESS_TOKEN` | unset | Optional bearer token; required when the target enables authentication |
| `RATE` | `250` | Iterations started per second |
| `DURATION` | `30m` | Constant-arrival-rate duration |
| `PREALLOCATED_VUS` | `300` | Virtual users allocated before the run |
| `MAX_VUS` | `1000` | Maximum virtual users k6 may allocate |

The checked-in thresholds always require p95 below 250 ms and a failed-request rate
below 0.1%. Also inspect whether k6 reports dropped iterations: a latency threshold can
pass even when the load generator lacks enough virtual users to maintain the requested
arrival rate.

:::caution
Use invented data and an isolated performance store. Never point the k6 scenario or a
scale exercise at a clinical, shared or production registry. Load tests create real
traffic and may affect availability or cost.
:::

## Reading a result

When recording a performance result, include:

- commit SHA, runtime and provider configuration;
- store population size and candidate-density characteristics;
- load-generator and service regions;
- requested and achieved request rate;
- p50, p95 and p99 latency, failure rate and dropped iterations; and
- service resources, replica counts, throttling, errors and provider metrics.

An end-to-end failure needs boundary-level diagnosis. Compare API traces, provider
latency, candidate counts, rate-limiter rejections, CPU and allocation data before
changing the scoring engine.

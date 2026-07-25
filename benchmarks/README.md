# Performance gates

`FhirMpi.Benchmarks` measures the pure scoring path over 500 pre-normalised
candidates. The checked-in baseline and `scripts/Test-BenchmarkRegression.ps1`
enforce both the 10 ms ceiling and the 10% regression gate.

The k6 scenario exercises the end-to-end `$match` target:

```powershell
$env:BASE_URL = "https://fhir-mpi.example"
$env:ACCESS_TOKEN = "<short-lived system token>"
k6 run benchmarks/k6/match.js
```

Defaults are 250 requests/second for 30 minutes, p95 below 250 ms, and fewer
than 0.1% failed requests. Run it in the same region as a pre-seeded isolated
GCP test store containing synthetic data at the intended scale. It must never
target a clinical or production registry.

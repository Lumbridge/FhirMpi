param(
    [string] $BaselinePath = "benchmarks/baseline.json",
    [string] $ResultsDirectory = "BenchmarkDotNet.Artifacts/results"
)

$ErrorActionPreference = "Stop"

$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
$report = Get-ChildItem -LiteralPath $ResultsDirectory -Filter "*-report-full*.json" |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($null -eq $report) {
    throw "No BenchmarkDotNet full JSON report was found in '$ResultsDirectory'."
}

$results = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
$benchmark = $results.Benchmarks |
    Where-Object { $_.Method -eq $baseline.benchmark -or $_.FullName -like "*.$($baseline.benchmark)" } |
    Select-Object -First 1

if ($null -eq $benchmark -or $null -eq $benchmark.Statistics.Mean) {
    throw "Benchmark '$($baseline.benchmark)' was not present in '$($report.FullName)'."
}

$meanNanoseconds = [double] $benchmark.Statistics.Mean
$nanosecondsPerMillisecond = 1000000.0
$meanMilliseconds = $meanNanoseconds / $nanosecondsPerMillisecond
$regressionPercent =
    (($meanNanoseconds - [double] $baseline.baselineMeanNanoseconds) /
        [double] $baseline.baselineMeanNanoseconds) * 100

Write-Host (
    "Benchmark mean: {0:N3} ms; baseline: {1:N3} ms; change: {2:N1}%" -f
    $meanMilliseconds,
    ([double] $baseline.baselineMeanNanoseconds / $nanosecondsPerMillisecond),
    $regressionPercent)

if ($meanMilliseconds -gt [double] $baseline.maximumMeanMilliseconds) {
    throw "Core scoring exceeded the $($baseline.maximumMeanMilliseconds) ms performance gate."
}

if ($regressionPercent -gt [double] $baseline.maximumRegressionPercent) {
    throw "Core scoring regressed by more than $($baseline.maximumRegressionPercent)%."
}

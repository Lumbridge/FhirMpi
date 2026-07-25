using BenchmarkDotNet.Running;

BenchmarkSwitcher
    .FromAssembly(typeof(FhirMpi.Benchmarks.MatchingBenchmarks).Assembly)
    .Run(args);

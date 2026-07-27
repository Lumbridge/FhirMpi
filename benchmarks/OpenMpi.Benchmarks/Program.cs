using BenchmarkDotNet.Running;

BenchmarkSwitcher
    .FromAssembly(typeof(OpenMpi.Benchmarks.MatchingBenchmarks).Assembly)
    .Run(args);

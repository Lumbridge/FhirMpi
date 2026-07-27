using BenchmarkDotNet.Running;

BenchmarkSwitcher
    .FromAssembly(typeof(UnifyEmpi.Benchmarks.MatchingBenchmarks).Assembly)
    .Run(args);

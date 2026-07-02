using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Cairn.Benchmarks.EndToEndBenchmarks).Assembly).Run(args);

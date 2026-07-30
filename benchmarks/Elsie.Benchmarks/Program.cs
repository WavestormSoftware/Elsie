using BenchmarkDotNet.Running;
using Elsie.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5 (25F71) [Darwin 25.5.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 8.0.22 (8.0.22, 8.0.2225.52707), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 8.0.22 (8.0.22, 8.0.2225.52707), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------- |-----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| ImmediateJobs |  0.9994 ns | 0.1848 ns | 0.0101 ns |  1.00 |    0.01 |      - |         - |          NA |
| Hangfire      | 28.0701 ns | 5.5609 ns | 0.3048 ns | 28.09 |    0.36 | 0.0038 |      32 B |          NA |
| Quartz        |  0.0521 ns | 0.3861 ns | 0.0212 ns |  0.05 |    0.02 |      - |         - |          NA |

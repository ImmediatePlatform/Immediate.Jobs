```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5 (25F71) [Darwin 25.5.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 8.0.22 (8.0.22, 8.0.2225.52707), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 8.0.22 (8.0.22, 8.0.2225.52707), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Gen2   | Allocated | Alloc Ratio |
|-------------- |----------:|----------:|----------:|------:|--------:|-------:|-------:|-------:|----------:|------------:|
| ImmediateJobs |  3.796 μs |  1.883 μs | 0.1032 μs |  1.00 |    0.03 | 0.6256 | 0.1526 | 0.0153 |   5.07 KB |        1.00 |
| Hangfire      | 16.122 μs | 41.167 μs | 2.2565 μs |  4.25 |    0.52 | 1.8005 | 0.4578 | 0.0305 |  14.49 KB |        2.86 |
| Quartz        | 17.288 μs | 43.929 μs | 2.4079 μs |  4.56 |    0.56 | 0.7935 | 0.2136 | 0.0610 |   6.34 KB |        1.25 |

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5 (25F71) [Darwin 25.5.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]   : .NET 8.0.22 (8.0.22, 8.0.2225.52707), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 8.0.22 (8.0.22, 8.0.2225.52707), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | Mean        | Error         | StdDev     | Ratio | RatioSD | Gen0   | Gen1   | Gen2   | Allocated | Alloc Ratio |
|-------------- |------------:|--------------:|-----------:|------:|--------:|-------:|-------:|-------:|----------:|------------:|
| ImmediateJobs |   393.60 ns |    370.181 ns |  20.291 ns |  1.00 |    0.06 | 0.0753 | 0.0024 | 0.0010 |     649 B |        1.00 |
| Hangfire      | 7,765.75 ns | 11,777.625 ns | 645.571 ns | 19.77 |    1.68 | 0.3662 |      - |      - |    3104 B |        4.78 |
| Quartz        |    11.67 ns |      5.718 ns |   0.313 ns |  0.03 |    0.00 | 0.0162 |      - |      - |     136 B |        0.21 |

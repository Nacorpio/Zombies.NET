# Performance budgets and measurements

The slice's budgets come from the spec (#2). This page records what was measured against each, on what, and how to measure it again.
A number here is only as good as its method, so each row says what was and was not measured.

## Hardware

Every measurement below was taken on **one machine, the recommended tier**: Intel Core i5-7400 (4 cores), NVIDIA GeForce GTX 1060 6 GB,
16 GB RAM, Windows 10, .NET 10.0.400, Release builds.

**The floor tier (a dual-core i3-6100 with a GTX 750 Ti or an Intel UHD 630, 8 GB) has not been measured.** No one has run the slice on it, so
the claim "the floor tier holds 60 FPS at 720p" is not established. See #153.

## Budgets against measurements

| Budget (spec) | Measured | Verdict |
| --- | --- | --- |
| Frame 16.6 ms at 1080p (recommended tier) | average 1.54 ms, p50 1.45, p95 2.22, p99 3.16; one frame of 54.13 ms | Inside, with a large margin. One hitch: #154 |
| Frame 16.6 ms at 720p (floor-tier resolution, recommended hardware) | average 1.22 ms, p50 1.13, p95 1.83, p99 2.80; one frame of 53.46 ms | Inside on this hardware. **Not** a floor-tier result: #153 |
| Main-thread CPU 8 ms | not measured on its own | Unmeasured |
| Render submit 3 ms | `--bench` prints render time only for slow frames: 0.46 to 1.38 ms | Inside for the frames shown; not measured for every frame |
| GPU 14 ms | not measured (no GPU timestamp queries) | Unmeasured: #153 |
| Server tick 12 ms at 4 players and 200 zombies | mean 2.28 ms, p50 2.17, p95 2.76, p99 3.20, max 5.41 (idle machine) | Inside. Not robust under CPU load: #152 |
| Chunk meshing 2 ms per chunk on a worker | light+mesh mean 1.96 to 2.45 ms over six one-pass runs (p95 3.3 to 4.0 ms); 0.71 ms per chunk in BenchmarkDotNet steady state | **At the budget, and the two methods disagree: #151** |
| 3 GB RAM | peak working set 209 MB at 1080p, 221 MB at 720p during `--bench` | Inside, but the bench has no zombies, village or UI in play |
| 1.5 GB VRAM | not measured | Unmeasured: #153 |
| No steady-state allocation in hot paths | server tick 0 bytes over 900 ticks on an idle machine; fails under CPU load | Holds only on an idle machine: #152 |
| 10 second startup | client 3.2 to 3.3 s from process start to the first drawn frame (4 runs); dedicated Server 1.5 s warm over 3 runs, 3.3 s for the first (cold) run | Inside |

What the frame measurements cover: the world as the client draws it by default. **The running game has no zombies, no village loot and no combat yet**
(#148, #149, #150), so the frame time with a full slice on screen is not known and these numbers are a lower bound.

## How each number was taken

Build everything in Release first: `dotnet build Zombies.slnx --configuration Release`.

| What | Command |
| --- | --- |
| Frame time | `src/Zombies.Client/bin/Release/net10.0/Zombies.Client.exe --bench --size 1920x1080`, then `--size 1280x720` |
| Server tick, 4 players, 200 zombies | `dotnet run --project tools/SimHarness --configuration Release --no-build -- ai 12345 --ticks 900 --budget-ms 12` |
| Chunk meshing, one pass | `dotnet run --project tools/SimHarness --configuration Release --no-build -- worldgen 12345 4 --budget-ms 2` |
| Chunk meshing and ECS iteration, steady state | `dotnet run --project tools/Benchmarks --configuration Release --no-build` |
| Memory | the peak working set of the client process during `--bench` |
| Startup | wall time of `Zombies.Client.exe --frames 1 --size 1280x720`, and of `Zombies.Server.exe --port 0 --ticks 1 --mods mods` |

## Benchmarks that fail CI

`tools/Benchmarks` runs BenchmarkDotNet (in process, short job) over chunk meshing and ECS iteration, then compares each result with
`tools/Benchmarks/thresholds.json` and exits non-zero when one is over its limit. CI runs it on Windows and Linux.

Measured on the recommended rig, and the limits set against them:

| Benchmark | Mean | Allocated | Limit (mean) | Limit (allocated) |
| --- | --- | --- | --- | --- |
| Light and mesh a 5 by 5 patch of chunks (25 chunks) | 17.7 ms | 5.33 MB | 50 ms | 6.1 MB |
| ECS: walk one component, 200 entities | 1.2 us | 64 B | 10 us | 64 B |
| ECS: walk one component, 2,000 entities | 12.7 us | 64 B | 100 us | 64 B |
| ECS: walk and look up another, 200 entities | 4.7 us | 64 B | 40 us | 64 B |
| ECS: walk and look up another, 2,000 entities | 46.7 us | 64 B | 400 us | 64 B |

How the limits work, and where they are weak:

- Times are **absolute, not compared with an earlier run**, because CI machines differ from one another and from this one. The meshing limit is the
  spec's budget itself (25 chunks at 2 ms). The ECS limits are about eight times the measured mean: wide enough that machine noise does not fail CI,
  narrow enough that an accidental quadratic walk does. A regression of less than that margin will not be caught.
- Allocations are deterministic, so their limits are tight. The 64 bytes are the enumerator `EcsWorld.Query` returns; a system that queries once
  per tick allocates that much, so hot paths that must not allocate should not use `Query`.
- A benchmark with no limit, and a limit with no benchmark, both fail, so a renamed or deleted benchmark cannot quietly stop being checked.
  `tests/Benchmarks.Tests` covers this, and the pass and fail directions were checked by running the tool against a deliberately tightened limit.
- The benchmark and the harness measure meshing differently and disagree: #151.

## Not in CI, and why

`SimHarness ai` (the server tick at 4 players and 200 zombies) is not a CI step. It was broken on `main` until this work (it did not register the
sample Code mod's Trait, so it crashed on start), and fixed here, but its allocation and p99 checks fail intermittently when the machine is busy,
the same way the allocation tests have failed intermittently on Ubuntu CI. Adding it would make CI unreliable. Fixing that is #152.

The worldgen step in CI uses a 12 ms meshing budget, not 2 ms, so it cannot notice the overrun in #151.
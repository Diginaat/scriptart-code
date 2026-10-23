# Performance

## Method

`tests/ScriptArt.PerformanceTests` builds synthetic graphs where each statement is a Print Line fed by a Concatenate of two literals (4 nodes and 4 connections per statement, plus Entry). Each operation runs once to warm up, then seven times; the median is reported. Allocation is measured on the calling thread for one compile. Release build, no debugger.

Since 2026-10-23 the harness runs every measurement twice and reports the second pass, so tiered JIT compilation has optimized the code; single-graph compile times dropped for that reason, not because the compiler changed.

```sh
dotnet run -c Release --project tests/ScriptArt.PerformanceTests
dotnet run -c Release --project tests/ScriptArt.PerformanceTests -- --check tests/ScriptArt.PerformanceTests/baseline.json
dotnet run -c Release --project tests/ScriptArt.PerformanceTests -- --write-baseline tests/ScriptArt.PerformanceTests/baseline.json
```

`--check` fails (exit code 1) when a time exceeds its baseline more than twice (times under 1 ms are ignored as noise) or an allocation by more than 25%. Times depend on the machine: the checked-in baseline was recorded on the machine below, so rewrite it before checking on another one, and rewrite it in the same change as an intended slowdown.

## Results (2026-10-23)

Same machine as below.

| Measurement | 1,001 nodes | 10,001 nodes |
| --- | ---: | ---: |
| Serialize | 0.8 ms | 16.4 ms |
| Deserialize | 3.4 ms | 53.5 ms |
| Compile + emit | 3.0 ms | 61.4 ms |
| Clone (snapshot copy) | 0.2 ms | 0.9 ms |
| Allocated per compile | 2.8 MB | 28.1 MB |

| Project of 20 graphs × 1,001 nodes | Time |
| --- | ---: |
| Cold compile (parallel; 137 ms sequential) | 94.8 ms |
| One graph changed, same objects (every graph re-hashed) | 15.9 ms |
| One graph changed, snapshots (as the IDE compiles) | 3.6 ms |

| Spatial index, 10,001 nodes | Time |
| --- | ---: |
| Build | 1.5 ms |
| Query of a 1600×900 view | 0.01 ms |

## Results (2026-10-22)

Hardware: AMD Ryzen 5 7600X (6 cores / 12 threads), 31 GB RAM, Windows 11 IoT Enterprise LTSC 2024, .NET 10.0.12.

| Nodes | Serialize | Deserialize | Compile + emit | Allocated per compile |
| ---: | ---: | ---: | ---: | ---: |
| 1,001 | 1.2 ms | 7.7 ms | 13.8 ms | 2.5 MB |
| 10,001 | 13.6 ms | 72.0 ms | 74.7 ms | 24.3 MB |

Compilation scales roughly linearly. Roslyn verification is not included (it runs only on Generate/Build, on a background thread; the first call pays a one-time cost to load reference assemblies).

## Canvas frame time (2026-10-22)

Measured by hand in the Release build with View → Frame Statistics, which times `NodeCanvas.Render` on the UI thread. That is the cost of building the frame's drawing commands; Avalonia rasterizes them afterwards on its render thread, which this does not include. There are no automated UI benchmarks (see TESTING.md).

```sh
dotnet run -c Release --project tests/ScriptArt.PerformanceTests -- --write-graph 2500 <project>/Graphs/Program.graph.json
```

Graph of 10,001 nodes and 10,000 wires at 20% zoom with about 200 nodes and 210 wires on screen, measured over a marquee drag (median of the last frames):

| Change | Median render |
| --- | ---: |
| Before | 56.6 ms |
| Connected pins kept in a set instead of scanning all connections for every visible pin | 12.6 ms |
| Minimap nodes cached as one geometry per category; wires culled by their nodes' bounds before building the path | 5.5 ms |

The first frame after opening or changing the graph is slower (about 40 ms here) because it rebuilds the cached minimap geometry. The remaining cost was still linear in the graph size: every node and connection was checked against the viewport each frame. Since then the canvas queries a spatial index (D-040); the frame time has not been measured by hand again.

## Editor design choices

- One drawing surface; no control per node (D-002).
- Nodes and wires outside the viewport are skipped; the fine grid is skipped below 60% zoom.
- Text layouts and pens are cached; node layouts are rebuilt only on graph changes, and only for moved nodes during a drag.
- Dragging updates positions directly and records one undo step on release.
- Live compilation is debounced (250 ms) so bursts of edits compile once, and runs on a background thread against a snapshot (D-041).
- Culling, the marquee, and hit-testing query a grid index of node and wire bounds (D-040).
- The minimap draws nodes from a cached geometry per category color, rebuilt only when layouts change, plus one rectangle per comment.
- Which pins are connected is kept in a set rebuilt with the layouts.

## Not yet measured

Rasterization time and hit-testing are not measured, nor is the canvas frame time since the spatial index. Known cost to address when measurements justify it: every graph change rebuilds all node layouts and the index. Planned: incremental layout updates.

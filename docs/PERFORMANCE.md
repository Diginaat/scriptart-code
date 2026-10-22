# Performance

## Method

`tests/VisualStudioLikeIDE.PerformanceTests` builds synthetic graphs where each statement is a Print Line fed by a Concatenate of two literals (4 nodes and 4 connections per statement, plus Entry). Each operation runs once to warm up, then seven times; the median is reported. Allocation is measured on the calling thread for one compile. Release build, no debugger.

```sh
dotnet run -c Release --project tests/VisualStudioLikeIDE.PerformanceTests
```

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
dotnet run -c Release --project tests/VisualStudioLikeIDE.PerformanceTests -- --write-graph 2500 <project>/Graphs/Program.graph.json
```

Graph of 10,001 nodes and 10,000 wires at 20% zoom with about 200 nodes and 210 wires on screen, measured over a marquee drag (median of the last frames):

| Change | Median render |
| --- | ---: |
| Before | 56.6 ms |
| Connected pins kept in a set instead of scanning all connections for every visible pin | 12.6 ms |
| Minimap nodes cached as one geometry per category; wires culled by their nodes' bounds before building the path | 5.5 ms |

The first frame after opening or changing the graph is slower (about 40 ms here) because it rebuilds the cached minimap geometry. The remaining cost is still linear in the graph size: every node and connection is checked against the viewport each frame.

## Editor design choices

- One drawing surface; no control per node (D-002).
- Nodes and wires outside the viewport are skipped; the fine grid is skipped below 60% zoom.
- Text layouts and pens are cached; node layouts are rebuilt only on graph changes, and only for moved nodes during a drag.
- Dragging updates positions directly and records one undo step on release.
- Live compilation is debounced (250 ms) so bursts of edits compile once.
- The minimap draws nodes from a cached geometry per category color, rebuilt only when layouts change, plus one rectangle per comment.
- Which pins are connected is kept in a set rebuilt with the layouts.

## Not yet measured

Rasterization time, and hit-testing, are not measured. Known costs to address when measurements justify it: viewport culling, hit-testing, and wire drawing are linear in the number of nodes/connections; every graph change rebuilds all node layouts; live compilation runs on the UI thread (about 75 ms at 10,000 nodes). Planned: spatial index, incremental layout updates, background compilation.

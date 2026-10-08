# Animation performance

The overlay uses `CompositionTarget.Rendering` to follow WPF's composition clock, instead of a fixed 16 ms UI timer. Keyboard events wake the dispatcher directly. Trails animate continuously while visible; hiding the overlay or finishing all trails detaches the animation callback.

Key labels, brushes, clipping geometry and idle/held key graphics are cached. Count text is rebuilt when its value changes. A trail list is grouped by lane once per frame, rather than scanned once for each lane.

## Local comparison

One local comparison used a 100 Hz display and seven lanes with a nominal 40 completed notes/second plus dense demo trails. Six-second runs excluded the first second from frame and drawing-time samples.

| Metric | Original timer renderer | Composition renderer |
| --- | ---: | ---: |
| UI drawing submissions/second | 39.01 | 99.78 |
| Median frame interval | 30.286 ms | 9.975 ms |
| 95th percentile frame interval | 39.281 ms | 10.645 ms |
| Median UI drawing time | 1.044 ms | 0.052 ms |
| 95th percentile UI drawing time | 2.382 ms | 0.444 ms |
| Total process CPU time in six seconds | 1203.1 ms | 1625.0 ms |

The optimized renderer submits more frames and therefore uses more total CPU time in this run, despite reducing UI work per frame. These measurements are application drawing submissions, not GPU presentation or display frame counters. Refresh rate, composition, remote viewing and game load can change the result.

## Benchmark the current renderer

On an interactive Windows desktop, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/build-benchmark.ps1
$benchmarkProcess = Start-Process ./artifacts/benchmark.exe -ArgumentList 'artifacts/benchmark.txt' -WindowStyle Hidden -PassThru -Wait
Get-Content ./artifacts/benchmark.txt
```

The benchmark briefly shows an overlay. It simulates its own notes without injecting physical keyboard input. Reports and executables stay in the ignored `artifacts` directory.

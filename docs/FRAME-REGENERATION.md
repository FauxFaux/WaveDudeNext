# Frame regeneration and browser recording storage

Checkpoint-and-replay is a possible later optimisation. It is not implemented: the first
browser recording path writes sampled frames to OPFS and increases the sampling interval
to fit an estimated storage budget. There is no RAM frame cache or checkpoint restoration.

## Raw recording size

The [Bragg Reflector example](../web/src/assets/examples/Example13_Bragg_reflector.json#L8-L20)
uses a 1400 × 1080 canvas, 2500 steps and `visualizeEvery = 2`. With the existing zero-based
capture convention, the frame count is `ceil(steps / visualizeEvery)`, or 1250.
The [Wasm export](../WaveDudeNext.Wasm/Program.cs#L68-L90) returns two row-major planes:
field amplitude and square-root intensity, each with one f16 per pixel. These are numerical
samples, coloured by the renderer at display time, rather than encoded pictures. See
[FrameShape](../WaveDudeNext/Engine/FrameShape.cs#L24-L73).

Assuming incompressible data and ignoring metadata:

| Stored data | Bytes per frame | Bytes for 1250 frames | GiB |
| --- | ---: | ---: | ---: |
| One f16 plane | 3,024,000 | 3,780,000,000 | 3.52 |
| Field and √intensity | 6,048,000 | 7,560,000,000 | 7.04 |

Recording every step doubles the totals. Solver state, Wasm runtime memory, rendering
buffers and temporary copies are additional allocations.

## Deferred option: checkpoint and replay

A displayed frame is not a restartable solver checkpoint. The wave equation needs both
the current and previous field; intensity is a running filter with internal history.
The current engine exposes reset and frame capture, but no checkpoint/restore operation.

A future checkpoint would preserve:

| State | Reason and source |
| --- | --- |
| Current and previous field, f32 | The two evolving wave grids, swapped after each substep: [WaveSimulator](../WaveDudeNext/Engine/WaveSimulator.cs#L784-L853), [field declarations](../WaveDudeNext/Engine/WaveSimulator.cs#L1070-L1074). |
| Both internal intensity stages, f32 | The recursive filter state is distinct from its displayed blend: [declarations](../WaveDudeNext/Engine/WaveSimulator.cs#L1079-L1097), [update](../WaveDudeNext/Engine/WaveSimulator.cs#L882-L893). The output blend can be reconstructed. |
| Full grid, including padding and any external damping rim | A frame capture copies only the canvas interior: [CopyInterior](../WaveDudeNext/Engine/WaveSimulator.cs#L268-L315). The scene can extend outside the canvas: [SceneData](../WaveDudeNext/Engine/SceneData.cs#L32-L44). |
| Elapsed step/time | Source phase and stepping depend on time: [AdvanceOneStep](../WaveDudeNext/Engine/WaveSimulator.cs#L353-L365). |
| Source RNG state, cycle anchors and current off-widths | Pulsed sources can change their timing during a run: [ComputePulsePosition](../WaveDudeNext/Engine/WaveSimulator.cs#L624-L700), [state declarations](../WaveDudeNext/Engine/WaveSimulator.cs#L1133-L1149). The project seed alone does not describe the current generator state. |
| Exact project and solver/build identity | Reconstruct the fixed scene maps, emitters and numerical options using the same implementation. An edited project or different solver must not silently reuse incompatible checkpoints. |

Integrated probe results would also need their accumulated totals and sampling state;
see [probe sampling](../WaveDudeNext/Engine/WaveSimulator.cs#L398-L435). Merely drawing
reconstructed field/intensity frames would not require restoring the whole probe recording.

Checkpoint arrays should retain native f32 bits. Saving f16 display samples as solver state
would introduce rounding into the resumed simulation. Checkpoints should be taken at a
defined completed-step boundary, not partway through a substep or source update.

### Worked replay example

For a hypothetical recording long enough to contain frame 2432:

1. Look for frame 2432 in the disposable frame store.
2. If missing, restore the nearest preceding checkpoint, for example frame 2400.
3. Advance 32 capture intervals. At `visualizeEvery = 2`, this is 64 calls to
   `AdvanceOneStep`, including the solver's internal substeps.
4. Capture frame 2432 using the normal f16 capture path and display it. Intermediate
   frames could be retained if useful for nearby seeks.

The checkpoint must correspond to the precise captured engine state. In the current export,
frame k is captured after `k * visualizeEvery + 1` advances from reset; the UI labels its
zero-based step as `k * visualizeEvery`. The +1 does not change the 64-step difference.
The actual Bragg example has only frames 0 through 1249.

With checkpoints every 100 frames, a miss needs at most 99 capture intervals of replay,
or 198 full simulation steps at an interval of 2. Seek latency depends on measured Wasm
solver throughput; storage savings alone do not establish that the delay is acceptable.

Four f32 planes cost `width * height * 4 * 4` bytes: about 24.2 MB per checkpoint before
padding. For this example, including the one-cell border and no external rim, that is
`1402 * 1082 * 16 = 24,271,424` bytes. Checkpoints at frames 0, 100, …, 1200 total 13:
315,528,512 bytes, about 0.294 GiB, plus metadata and source state. This is roughly 4% of
the complete two-plane f16 recording, at the cost of replay work.

Before implementing this option, compare frames after restore/replay with an uninterrupted
run using the same solver build. Cross-build or cross-engine bitwise equivalence is not
assumed. Scene reconstruction, versioning, cancellation and eventual probe recovery are
additional work, which is why this design is deferred.

## Eviction and memory limits

An application can keep checkpoints while deleting ordinary cached frames. Browser-driven
storage eviction is a different mechanism: the normal OPFS directory does not offer
per-file eviction priorities. The [Storage Standard](https://storage.spec.whatwg.org/#storage-pressure)
defines bucket eviction and persistence; persistence is requested, not guaranteed to be granted,
and users can still clear stored data.

Chromium's [Storage Buckets API](https://developer.chrome.com/docs/web-platform/storage-buckets)
allows separate buckets with different persistence policies. A future implementation could
request a persistent checkpoint bucket and use a disposable frame bucket, with feature
detection and a fallback to origin-wide persistence plus application-managed deletion.
The first implementation does not request persistence or use storage buckets.

`navigator.storage.estimate()` reports estimated origin storage usage and quota, not free
RAM or physical free disc space. A reported quota is not a reservation; writes can still
fail. There is no reliable portable query for the additional RAM a tab can safely allocate.
`deviceMemory` is a coarse device hint, and a JS heap limit is not a total tab allowance.
[Application memory measurement](https://web.dev/articles/monitor-total-page-memory-usage)
is useful for usage diagnostics, with limited support and cross-origin isolation requirements.
A single wasm32 linear memory has a [4 GiB addressing ceiling](https://v8.dev/blog/4gb-wasm-memory),
which is separate from total browser process memory.

## First implementation: sampled frames on disc

The [worker](../web/src/Wasm/Simulation.worker.ts#L24-L51) estimates storage before stepping:

```text
budget = floor(max(0, quota - usage) / 2)
frameBytes = width * height * 2 planes * 2 bytes
maxFrames = min(requestedFrames, floor(budget / (frameBytes + 4096)))
effectiveInterval = max(requestedInterval, ceil(requestedSteps / maxFrames))
frameCount = ceil(requestedSteps / effectiveInterval)
```

Half the estimated headroom is left unused, and each frame is charged an extra 4096 bytes
as a simple file-overhead allowance. These are conservative planning choices, not a guarantee
of enough physical space. If the estimate is unavailable or cannot accommodate one frame,
the run reports an error. A write failure stops recording and leaves already published
frames available; it does not silently fall back to RAM.

For example, with 2 GiB of reported unused quota, the budget is 1 GiB. A 1400 × 1080 frame
costs 6,052,096 bytes including the allowance, so the capacity is 177 frames. For 2500 steps,
the interval becomes 15 and the run stores 167 frames: 1,010,016,000 raw bytes, about 0.941 GiB.

The [export](../WaveDudeNext.Wasm/Program.cs#L38-L65) increases only the submitted run's capture
interval, leaving the editable project's settings unchanged. The run ends at the last sampled
step; an unsampled tail is not computed. A budget of one frame captures only frame zero.
The effective interval is shown in the UI and used by the timeline's frame-to-step mapping.

[FrameStore.ts](../web/src/Wasm/FrameStore.ts) writes one paired-frame binary file per index
under `wavedude-frames/<run-id>/`. Each file is closed before the worker announces it as stored.
Writes are sequential, so the producer cannot accumulate an unbounded pending-write queue.
The UI reads the selected file into the existing `FramePair` layout and uses the existing
renderers. It retains the displayed picture, with one read in flight and at most one pending
selection; there is no RAM frame cache, replay, automatic playback or recording recovery.

New runs, project replacement and unmount remove the previous recording. Stop and edits retain
published frames for inspection while terminating the worker. A reload or crash can leave
orphaned directories; automatic orphan discovery and recovery are deferred. Browser site-data
controls can clear these temporary recordings. Other tabs use distinct run directories.

The [OPFS guide](https://web.dev/articles/origin-private-file-system) describes byte-range and
worker file access for a possible later single-file store. The first implementation uses
closed files to keep simultaneous recording and display simple. The Cache API recommendation
in [the model-cache reference](../web/.model-cache.md) concerns immutable downloaded models;
it does not establish which API is fastest for this recording workload.

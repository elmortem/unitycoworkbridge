# Coordinator: measured hashing stalls and correction

Date: 2026-09-13. Unity 2022.3.62f2, Windows. Package 0.26.4; plugin 1.23.2.

## Reproduction and result

The test fixture contains 128 deterministic 1 MiB binary files and 2,000 small source files:
2,128 files, 134,280,618 bytes. It lives in `AgentBridgeUnity/Temp/AgentBridge/CoordinatorPerfFixture`;
it is not imported and is not part of the project's normal evidence roots.

| Measurement | Original | Optimized |
| --- | ---: | ---: |
| One input snapshot | 8,462 ms on editor thread | 397 ms wall time on worker |
| Two successive snapshots | 16,893 ms on editor thread | 863 ms wall time on workers |
| Editor updates during the two reads | blocked by synchronous calls | 26 |
| Largest observed editor-update gap during optimized reads | — | 42 ms |

The digest is identical to the original implementation:
`71ce4f2c346b069bfd55d1eb7c7a0dac98f59513b395a0bd1cd3af58bc409553`.
This fixture demonstrates roughly 19.6x less elapsed hashing time, in addition to removing
hashing from the editor thread. It is not a worst-case latency guarantee for arbitrary projects.
The original run also triggered the client's editor-wake attempts: the editor was busy, not asleep.

Baseline task: `Task_20260913_PerfBaseline`.
Final reproduction task: `Task_20260913_164730_496_coordinator_perf`.

Run from the repository with its test Unity Editor open:

```powershell
./scripts/verify-coordinator-performance.ps1
```

`-IncludeBlockingBaseline` deliberately executes the old synchronous SHA256Managed snapshot
twice for comparison; it can display Unity's Hold on window. The default only runs the corrected
worker path, checks the original digest and confirms that editor updates continue.

## Implementation

- `ContentHash`: Windows CNG SHA-256 behind `UNITY_EDITOR_WIN`; portable `SHA256.Create()` on
  macOS/Linux and a fallback if Windows cannot initialize CNG. Native imports and the native
  implementation are absent from non-Windows builds. No third-party native binary is shipped.
- `ValidationInputSnapshot`: one 64 KiB streaming buffer instead of allocating each complete
  input file. The persisted digest format remains compatible.
- `InputHashJob`: copies the input configuration before dispatch; a canceled/abandoned task
  cannot accidentally read the next task's roots. Unity APIs stay on the editor thread.
- Cache lookups, source fingerprints, test coalescing and final validation snapshots run on
  workers. A source fingerprint is shared within a lookup batch. An index miss avoids asset
  hashing. While an initial lookup is pending, the scheduler does not start the same request.
- After yielding, cache publication rechecks cancellation/task state, request bytes, evidence,
  artifacts, context and coordination authorization. An active filesystem observer protects
  the interval between capture and publication. Misses get a scheduler turn instead of an
  immediate loop of repeated hashing.
- Test finalization is recorded in SessionState and polled from `EditorApplication.update`,
  including after domain reload. Compile finalization also polls from update; it does not rely
  on inspector-driven `delayCall` scheduling to complete while the editor is in the background.
- Fast hashing waits for an in-progress Unity import to settle before accepting preparation.
- Directory timestamp-only notifications are ignored, because directory timestamps are not
  snapshot inputs. Directory create/delete/rename events and file changes remain observed.
  This fixes a reproduced false stale-input result from Unity touching the package directory
  during PlayMode reload.

The observed roots are `Assets`, `Packages`, `ProjectSettings`, and externally resolved local
packages. Registry package-cache directories are not added as roots. `Library`, `Temp`, `Logs`,
`obj`, and `Build` are excluded by the existing input policy. No whole-Library scan was added.

The referenced Backlinks package uses `AssetDatabase.GetAssetDependencyHash` plus the file's
modification time, or parallel MD5 reads. The first strategy describes imported assets; it is
not sufficient to authorize reuse immediately after an external source edit. This bridge keeps
content checks, including edits preserving length and timestamp.

## Validation

- Compile: `Task_20260913_134715_406_95ef633c`, success, valid evidence after domain reload.
- EditMode: `Task_20260913_134735_196_7dcc7501`, 30/30 passed, valid evidence. Includes native vs
  portable SHA-256, offsets/streaming/reuse, old digest compatibility, immutable worker inputs,
  directory timestamp handling, real file observation, evidence/cache and cancellation policy.
- PlayMode: `Task_20260913_134856_142_5984fe79`, 2/2 passed, valid evidence after domain reload.
- Actual PlayMode cache reuse: `Task_20260913_134300_896_647420bf`, 2/2 served from `Task_20260913_134234_479_bfc3ee44`,
  valid evidence, no new test run.
- `AgentBridgeCoordination.Tests --group state`: all 11 scenarios passed for each of
  `HashPlatform=UNITY_EDITOR_WIN`, `UNITY_EDITOR_OSX`, and `UNITY_EDITOR_LINUX`, built and run on
  Windows. Portable variants explicitly check that the Windows implementation is absent.
- CI now includes a Windows/macOS/Ubuntu matrix for the real host OS. Those remote runs and
  Unity Editor execution on macOS/Linux were not performed in this local session.
- Plugin ZIP built and verified with `scripts/build-plugin.ps1`; version and archive checks pass.

Two pre-existing test assumptions were corrected: filters no longer belong to the content
context (coverage handles selection), and tests publishing into the real cache use current
timestamps instead of immediately becoming the oldest entry in an already-full cache.

## Diagnostics

Editor telemetry now includes `input_hash` with stage, elapsed milliseconds, file count and byte
count; `coordinator_slow_stage` for synchronous update/queue/schedule/UI/screenshot stages taking
at least 50 ms; and `input_changes` with bounded changed-path samples. Unity Profiler markers use
the same stage names. These identify expensive stages without interpreting every stale heartbeat
as a sleeping editor.

Reference: [Windows CNG hashing](https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/nf-bcrypt-bcryptcreatehash).

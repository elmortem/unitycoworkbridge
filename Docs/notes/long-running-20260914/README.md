# Long-running task acceptance — 2026-09-14

Versions: Unity package **0.30.0**, CLI **1.21.0**, plugin **1.27.0**.

The bridge has no global execution timeout. A foreign agent can cancel a running test or cooperative C# task after 300 seconds of execution, including inside a still-active coordination window. The original owner can cancel earlier with its session. Cancellation retains editor ownership until execution and scene recovery finish. The old `TaskTimeoutSeconds` setting is ignored; queued work is not counted as execution time.

## Validation

| Check | Result |
| --- | --- |
| CLI build | Passed, no warnings or errors |
| CLI regression suite | Passed, including requester session propagation for `cancel` |
| Coordination suite, state and store groups | Passed, including a task surviving its window for one hour of simulated time and canceled batch fail-fast |
| Compile regression suite | 11/11 passed |
| Recovery and cancellation lifecycle suite | 7 passed, including ignored legacy deadlines, first cancellation reason retention, and finalization after recovery |
| Unity `TestCancellationPolicyTests` | 9/9 passed, valid evidence; task `Task_20260914_194215_248_4b3428a4` |
| Final Unity compile | Passed, valid evidence; task `Task_20260914_200711_987_c95b60d4` |
| Natural C# completion | Passed after 306.906 seconds; no automatic timeout |
| Early foreign cancellation | Rejected with `task_protected` |
| Owner cancellation, EditMode and PlayMode | Passed, subsequent marker tasks confirmed executor shutdown and scene recovery |
| Delayed C# cancellation | Stayed `canceling`, kept its follower queued, and finished `canceled` despite a late successful return |
| Foreign PlayMode preemption in a 600-second window | Passed at 306 seconds, with initiator and `preempted_after_300s` in the owner's result |
| Canceled package continuation | Closed with `failed:long:canceled`; the second step was never dispatched |
| Next agent | Its marker task succeeded after test shutdown and scene recovery |
| Plugin ZIP | Version, skill frontmatter, entry safety, and byte-for-byte source parity passed |

Final editor status: ready, no active task, outside Play Mode. The verifier registrations were closed. Pre-existing and concurrent unrelated repository changes were left intact.

## Saved evidence

- [Natural long C# result](csharp-over-300-success.json)
- [Early foreign C# refusal](early-foreign-csharp-rejected.json)
- [Delayed C# cancellation result](slow-csharp-canceled.json)
- [Owner PlayMode cancellation](play-owner-canceled.json)
- [Early foreign PlayMode refusal](early-foreign-cancel.json)
- [Status after protection elapsed](preemptible-status.json)
- [Still-granted window before cancellation](window-before-cancel.json)
- [Foreign cancellation acknowledgment](late-foreign-cancel.json)
- [Final canceled test and notification](play-final.json)
- [Closed package](batch-final.json)
- [Undispatched second step](unstarted-second-step.json)
- [Successful next agent](next-agent.json)

## Individual test timeout

Unity Test Framework 1.1.33 independently defaults to 180 seconds per test. Its `CoroutineTestWorkItem` synchronously drains the enumerator after the coroutine timeout. The initial verification fixture omitted an explicit NUnit timeout and therefore blocked the editor until its own loop ended. The queued foreign cancellation was then processed at 904 seconds; see [that diagnostic result](blocked-framework-late-preemption.json). This is not the responsive-preemption acceptance result above.

The corrected fixture uses `[Timeout(600000)]` for its 360-second fallback loop. It remained responsive past 180 seconds and was canceled by the neighbor at 306 seconds. The bridge preserves NUnit test-specific limits; intentionally long individual tests must declare a suitable `[Timeout(...)]` in milliseconds. This requirement is documented in the shipped skill and package guide.

## Reproduction

Use an idle AgentBridgeUnity editor with the current package and the Release CLI build:

```powershell
dotnet run --project AgentBridgeCli.Tests -c Release --no-restore
dotnet run --project AgentBridgeCoordination.Tests -c Release --no-restore -- --group all
dotnet run --project AgentBridgeRecovery.Tests -c Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-test-cancellation.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-long-running-tasks.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1 -ValidateOnly
```

The long verifier deliberately waits through real 300-second protection periods. It changes no project timeout settings and uses only the normal CLI for execution and cancellation.

ZIP: `unity-bridge-plugin/unity-bridge-plugin.zip`.

SHA-256: `ef2ed7406f4530648edd52342878e69d7ef6a0184df33cc39f60e38ed934754f`.

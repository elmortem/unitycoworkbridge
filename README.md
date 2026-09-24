# Unity Agent Bridge

A system for executing AI-generated C# scripts in an open Unity Editor. An agent writes the scripts, Bridge compiles and runs them inside Unity, then returns results and errors.

> **Breaking migration on the `roslyn-cli` branch:** the package ID changed from `com.elmortem.coworkbridge` to `com.elmortem.agentbridge`, and the project-local `bridge.sh` / `bridge.ps1` clients were replaced by the standalone `agentbridge` CLI. Remove the old package entry before installing the new package. This branch is under active development; use the branch-pinned UPM URL below until it is merged and tagged.

## How It Works

The system consists of three parts:

**Agent Bridge** — a C# package inside Unity Editor. A background coordinator picks up tasks dropped into `Library/AgentBridge/Inbox/`, one at a time. C# tasks are compiled in memory with Roslyn (no domain reload, no files written under `Assets`); `compile` and `tests` tasks use Unity's own compiler/test runner. Every task gets a single result record in `Library/AgentBridge/Journal/<TaskId>.json`.

**AgentBridge CLI** — a self-contained executable for Windows, macOS, and Linux. The stable command name is `agentbridge`. It discovers a Unity project from the current directory, writes protocol tasks atomically, waits for results, validates bridge health and protocol compatibility, and exposes `status` / `doctor` diagnostics. Use `--project <path>` when the current directory is outside the Unity project.

**Unity Bridge Plugin** — a plugin for Claude Agent. It ships two skills:

- `unity-bridge` — instructions for Claude on script generation, the client protocol, and error handling. It commands the Unity-side Bridge and auto-triggers on any Unity Editor task ("list all prefabs using shader X", "rename these assets", "compile the project", "run the tests"), or invoke it explicitly via `/unity-bridge`.
- `unity-ui` — declarative uGUI layout: creating/editing UI prefabs, dumping layout geometry, and screenshotting screens through `.ui.json` tasks (no C# compilation, no domain reload — iterations take seconds). Auto-triggers on layout phrasing ("build this popup", "move/recolor this element", "screenshot the screen"), or `/unity-ui`. uGUI + TMP only; UI Toolkit is not supported. See [Declarative UI Tasks](#declarative-ui-tasks).

There are eight task kinds, all created via the CLI and processed sequentially, one at a time:

- **`csharp`** — a `.cs` script; Bridge compiles it in memory with Roslyn and runs its `Run()` method on the main thread.
- **`ui`** — a `.ui.json` file; Bridge applies it to a prefab directly, without compilation.
- **`sceneshot`** — a `.sceneshot.json` file; Bridge screenshots the open scene from the requested Scene View angles, or the running Game View, without compilation.
- **`compile`** — verifies current compilation inputs and returns diagnostics, reusing a matching completed cycle (including automatic Unity compilation) or requesting a necessary run. Both success and errors are reusable without another reload. `Cached: true` and `SourceTaskId` identify reuse. Diagnostic `--fresh` requires a reason in `--note`; overlapping requests share a cycle completed while they waited. See [compilation and waiting](Docs/compilation.md).
- **`tests`** — runs EditMode or PlayMode tests and returns pass/fail counts and failure details in the same result record, surviving Play Mode's domain reload. Results are cached per mode: a repeat on an unchanged project — including a subset filter of a previous full run — is answered from cache, and a request compatible with a run already in flight attaches to it and shares its result. Any code or asset change invalidates the cache automatically; `--fresh` forces a real run.
- **`play`** / **`stopplay`** — open and close an owned play session, the only sanctioned way for an agent to reach Play Mode; both survive the domain reloads that entering and leaving it trigger. See [Play Mode](#play-mode).

- **`release`** — hands the editor back to the other agent sessions; it changes nothing in the project. See [Multi-Agent Sessions](#multi-agent-sessions).

## Installing AgentBridge CLI

Published releases contain self-contained binaries for Windows, macOS, and Linux on x64 and Arm64. The installer verifies the release checksum, installs into a per-user directory, and adds it to the user `PATH`.

Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/elmortem/unitycoworkbridge/roslyn-cli/scripts/install-agentbridge.ps1 | iex
```

macOS or Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/elmortem/unitycoworkbridge/roslyn-cli/scripts/install-agentbridge.sh | bash
```

Both installers always fetch the latest published release. To pin a specific one, pass `-Version 1.4.0` on Windows or set `AGENTBRIDGE_VERSION=1.4.0` on macOS/Linux.

Open a new terminal or restart the agent application, then verify:

```bash
agentbridge --version
```

### Updating

Once Unity Bridge is installed, the CLI can be updated from the Editor: **Tools → Agent Bridge → Update CLI**. It runs the same installer, pulls the latest release, and reports the result in the Console. Restart the agent application afterwards so it picks up the new binary.

If a GUI agent has not inherited the updated `PATH`, use the stable per-user install path directly: `%LOCALAPPDATA%\AgentBridge\bin\agentbridge.exe` on Windows or `$HOME/.local/bin/agentbridge` on macOS/Linux. The CLI is never discovered inside Unity's hashed `Library/PackageCache` path.

### Agents whose shell runs in a Linux sandbox

Some agents (Claude Cowork, dev containers, WSL) run the Editor on the host machine but give the agent a shell in a separate Linux environment where only the project folder is mounted. A host-native CLI is unreachable from there.

**Tools → Agent Bridge → Update CLI** therefore installs two builds: the native one for the current machine, and a `linux-x64`/`linux-arm64` build inside `<project>/Library/AgentBridge/cli/agentbridge`. That folder is inside the project, so the sandbox sees it through the same mount it already has, and the skills look for it as the last step of CLI discovery. `Library/` is not committed, so the extra binary never reaches the repository.

The bridge protocol is host-agnostic: `status.json` carries `ProjectId` (mirrored in `Library/AgentBridge/project-id`) and `HostOs`. When the CLI detects that the Editor reports a different operating system than its own, it identifies the project by `ProjectId` instead of comparing absolute paths, skips the Editor PID check — a host PID means nothing inside a container — and relies on the heartbeat for liveness with a wider tolerance. `agentbridge status` reports this as `Host: editor on windows, client on linux`. Both fields are optional: against an older package the CLI falls back to path comparison, and an older CLI ignores them.

For development directly from this checkout:

```bash
dotnet run --project AgentBridgeCli/AgentBridgeCli.csproj -- --project AgentBridgeUnity doctor
```

Release assets are produced by `.github/workflows/agentbridge-cli.yml` from tags named `agentbridge-v*`.

## Installing Unity Bridge

### Option 1: Via Package Manager (Git URL)

1. Open **Window → Package Manager** in Unity Editor
2. Click **+** → **Add package from git URL...**
3. Enter: `https://github.com/elmortem/unitycoworkbridge.git?path=/AgentBridgeUnity/Packages/com.elmortem.agentbridge#roslyn-cli`

### Option 2: Manual Copy

1. Copy the `com.elmortem.agentbridge/` folder into the `Packages/` folder of your Unity project

The package has no dependencies on other project assemblies and will work even if the project has compilation errors.

Roslyn is bundled in the package under `Roslyn~/` — nothing to download and no setup step.

## Installing Agent Plugin

### Requirements

Agent is only available in the Claude desktop application (macOS and Windows). The web version and mobile apps do not support Agent and plugins.

### Option 1: Via Claude Code CLI

If you have Claude Code installed, you can load the plugin directly from a local folder:

```bash
claude --plugin-dir /path/to/unity-bridge-plugin
```

For permanent installation, create your own marketplace or use the `--plugin-dir` flag on each launch.

### Option 2: Via Agent UI

1. Open Claude Desktop and go to the **Agent** tab
2. In the sidebar, click **Customize**
3. Click **Browse plugins** → upload the `unity-bridge-plugin/` folder or a `.zip` archive of it

### Option 3: Via Local Marketplace

If you want to distribute the plugin within a team:

1. Create a marketplace — a folder with a `.claude-plugin/marketplace.json` file containing a list of plugins
2. Add the marketplace to Claude Code: `/plugin marketplace add /path/to/marketplace`
3. Install the plugin: `/plugin install unity-bridge@marketplace-name`

### Plugin Structure

```
unity-bridge-plugin/
├── .claude-plugin/
│   └── plugin.json          ← plugin manifest
└── skills/
    ├── unity-bridge/
    │   └── SKILL.md         ← C# task instructions for Claude
    └── unity-ui/
        └── SKILL.md         ← declarative uGUI layout instructions
```

### Verifying Installation

After installation, just ask Claude to do something inside the Unity Editor (e.g. "list all prefabs using shader X" or "add a Rigidbody to all enemies") — the `unity-bridge` skill auto-triggers on such requests. You can also invoke it explicitly via `/unity-bridge`. If the plugin is installed correctly, Claude will start generating a script.

## Cancellation and long-running tasks

`agentbridge cancel <TaskId> [--session S]` addresses one task. Running tasks are protected against foreign cancellation for their first 300 seconds of actual execution, including tasks without a coordination window. After that, any agent may cancel a blocking test or cooperative C# task without asking its owner, even inside an unexpired window. The bridge checks the age; elapsed queue time does not count. The owner can cancel earlier using the same nonempty session, and human editor controls remain authoritative. An omitted session never proves ownership. Waiting tasks retain their existing window protection. Canceling one attached follower does not stop the shared test run. Repeated cancellation of a completed task is harmless.

Cancellation is serviced outside the execution queue. `cancel_requested` acknowledges the request; use `wait <TaskId>` for the actual outcome. While stopping, the target remains `canceling` and retains the editor until its executor stops and test scene recovery completes. After 30 seconds without completion, status reports `cancel_blocked:<TaskId>`; inspect Unity rather than deleting queue files or restarting the process. Other running task kinds currently return `cancel_not_supported_for_running_task`.

A test run whose Unity job ends without delivering results — a Test Framework failure, not a failing test — finishes as `runtime_error` no later than 25 seconds after the job stops, and the queue moves on by itself. No cancellation is needed, and an orphan `EditModeRunner` object left behind by such a job does not hold the queue.

Execution has no global time limit. The old `TaskTimeoutSeconds` setting is ignored, including values already saved in projects. Crossing 300 seconds or gaining a waiting neighbor never automatically stops a task. A coordinated window's deadline prevents new steps after expiry but does not stop its running task. Specialized operation safeguards (such as compilation startup and Play Mode transitions) remain in place. Client `--wait` only limits how long that client waits. Individual tests still obey NUnit/Test Framework timeouts. Unity Test Framework 1.1.33 defaults to 180 seconds per test; a deliberately long test must declare a suitable NUnit `[Timeout(...)]` in milliseconds (for example `[Timeout(1200000)]`). The bridge does not override an authored test timeout. In that framework version, timeout cleanup can synchronously drain the remaining test enumerator, blocking the editor until it returns.

With capability `long-running-tasks-v1`, `status` exposes `ActiveTaskElapsedSeconds` and `ActiveTaskCancelableByOtherAgents`. A foreign stop after the protected period ends with `canceled` and a `preempted_after_300s` message naming the initiator and elapsed seconds in the target's logs/result. `wait` delivers it to the owner and attached test followers; it is not a test failure or a success. A canceled batch does not dispatch its remaining steps. Agents may act on this status without additional approval and must not immediately resubmit a preempted package.

`ready` describes bridge availability. `QueueBlockReason` and `QueueBlockedSinceUtc` describe execution blockers, including test execution, cancellation, scene recovery and coordinated windows. Client `--wait` covers both queueing and execution, including automatic stopplay waiting, and leaves the original task intact on exit 2.

For a repeatable live acceptance run in the idle bridge test project, build the CLI and run `powershell -File scripts/verify-test-cancellation.ps1`. It runs explicit owner cancellation fixtures through the CLI and checks real executor/recovery state in subsequent tasks. Run `scripts/verify-long-running-tasks.ps1` for real runs beyond 300 seconds, early foreign rejection, late preemption, owner notification, and queue recovery.

## Usage

### Starting Bridge

In Unity Editor, open **Tools → Agent Bridge → Start**. Bridge will start watching `Library/AgentBridge/Inbox/`.

### Stopping Bridge

**Tools → Agent Bridge → Stop**

### Running Tasks via Agent

Just describe the task in natural language — the `unity-bridge` skill auto-triggers on Unity Editor requests:

```
add a Rigidbody component to all objects with the Enemy tag
```

If you want to force the skill to handle a request, invoke it explicitly:

```
/unity-bridge add a Rigidbody component to all objects with the Enemy tag
```

Claude will generate a script, hand it to the CLI, wait for the result, and show the outcome. If there are compilation errors, it will automatically fix the code and retry (up to 3 times).

### The CLI

One cross-platform command creates a task, waits for it, and prints the result to stdout. JSON is the default and remains the stable machine-readable contract. Add `--format human` to any command for a compact summary with actionable logs, diagnostics, test failures, and artifact paths. Run it from the Unity project root or any subdirectory. Outside the project, pass `--project <path>`.

```bash
agentbridge csharp Temp/AgentBridge/Task_20260226_143052_871_a3f.cs
agentbridge sceneshot Temp/AgentBridge/Task_20260226_143052_871_a3f.sceneshot.json
agentbridge compile --format human
agentbridge tests --mode EditMode --assembly MyGame.Tests --format human
agentbridge status
agentbridge doctor --format human
agentbridge release --session AB_20260813_1500_a1f
agentbridge play --seconds 30 --note "game shot of the main menu" --session AB_20260813_1500_a1f
agentbridge stopplay --session AB_20260813_1500_a1f
agentbridge coord status --session AB_20260813_1500_a1f --format human
```

Every command also accepts `--session <id>` and `--note <text>`, which identify the agent session behind the task; see [Multi-Agent Sessions](#multi-agent-sessions). The `coord` group is described in [Coordinating Several Agents](#coordinating-several-agents).

Exit codes: `0` success, `1` a terminal task failure including `test_failure`, `no_tests_matched`, `ambiguous_test_filter`, `stale_input` and `evidence_unavailable`, `2` client wait exhausted (the task is still running — retry with `agentbridge wait <TaskId>`), `3` project/bridge unavailable, protocol mismatch, or bad usage.

`tests --test` accepts an exact full test/fixture name or a unique short name. Every repeated `--test` must match at least one case within the assembly/category filters. An ambiguous name returns `ambiguous_test_filter` with candidate full names; use a full name or `--assembly` to disambiguate. A missing name or a zero-case run returns `no_tests_matched` and exit code `1`. The CLI also rejects an empty `success` response from older packages.

Short and full names both use the result cache. Each new result set stores the complete discovered test catalog for its mode, so alias resolution and coverage checks work without another Unity run. A subset is served only if every selected case is present, including parameterized cases. Project input changes invalidate reuse; filters select cases rather than change the input digest. Older cache entries without a catalog require one fresh run. `--fresh` still bypasses reuse.

An unknown option is now a usage error instead of a positional argument, so a misspelled `--sesion` fails loudly rather than becoming the name of a task file.

Typical successful human output is deliberately short, so agents do not need a second JSON parser just to report validation:

```text
compile: success (Task_20260805_092200_123_abcd1234, foreign errors: no)
tests: success (Task_20260805_092233_477_6cc4b1d1, 202 passed, 0 failed, 0 skipped, 0 inconclusive, 202 total, 4.103s)
tests: success (Task_20260805_092301_512_9ab2c3d4, cached from Task_20260805_092233_477_6cc4b1d1, 202 passed, 0 failed, 0 skipped, 0 inconclusive, 202 total, 4.103s)
```

Keep the default JSON format when another program needs the complete structured `TaskRecord` contract.

To stop Claude Code from asking for confirmation on every call, allow this exact command in your settings — `~/.claude/settings.json` (all projects) or `.claude/settings.local.json` (per project):

```json
{
  "permissions": {
    "allow": [
      "Bash(agentbridge:*)"
    ]
  }
}
```

`agentbridge status` validates the project path, package presence, Editor PID, heartbeat freshness and protocol version. `agentbridge doctor` additionally reports the CLI path/version, Unity/package versions, Roslyn readiness, capabilities, active task, and the two wake diagnostics described below (`Wake timer`, `Interaction mode`).

Beyond the fatal `Problems` that make the bridge unavailable, health also carries non-fatal `Warnings`. `doctor` prints them prefixed with `!` in human format and exposes them as `Health.Warnings` in JSON. They never affect `Ok`, `Code`, or the exit code:

| Warning | Meaning |
|---|---|
| `signal_tick_missing` | This Unity version has no internal `EditorApplication.SignalTick`; the bridge relies on the wake timer alone. |
| `wake_timer_missing` | The Editor runs on Windows but could not install its wake timer — background tasks may stall until the window is focused. |
| `interaction_throttled` | Preferences → General → Interaction Mode throttles the Editor's update loop in the background. |
| `editor_playing_manual` | The Editor is in play mode that no agent session owns — a manual start. Agent tasks wait behind it until something stops it; see [Play Mode](#play-mode). |

### Running While the Editor Is in the Background

Everything in the bridge rides on `EditorApplication.update`: the inbox scan, compile polling, task timeouts, awaited continuations and the heartbeat. Unity throttles that loop when its window loses focus and can stop calling it altogether when the window is minimized, so an unattended agent would otherwise watch its task sit in the queue until a human clicks into the Editor. Two layers keep the loop alive.

On the Editor side, `Wake timer: background_signal` means a timer independently calls Unity's internal, thread-safe `EditorApplication.SignalTick`. It is armed during initialization, before the first `update`, and uses `ActiveTickIntervalMs` while work is pending or running and `IdleTickIntervalMs` otherwise. The callback only signals Unity: queue processing, heartbeat writes and all other Editor APIs stay on the main thread. There is no persistent worker loop. Before domain reload or quit, shutdown drains any in-flight signal and prevents queued callbacks from signalling again. Turning the bridge off also stops the timer; Start arms it immediately.

On the client side, work commands enter recovery even if the heartbeat is already stale at submission. This only applies to a live, enabled, compatible editor on the same host whose project identity matches; other operational failures still prevent submission. Read-only `status` and `doctor` continue to report stale health without creating tasks. During queueing and execution, the CLI checks health every three seconds and starts wake attempts at a heartbeat age of five seconds. On Windows it posts `WM_NULL` up to five times, then tries focus once if the Editor is not already foreground. It restores the previous foreground window only if Unity still holds focus. Telemetry records whether the native call succeeded; only heartbeat progress proves recovery.

Exhausting wake attempts does not prove that Unity is asleep: import, compilation or domain reload can temporarily block heartbeat writes. Recovery waits up to 120 seconds without heartbeat progress, including when Unity is foreground, then returns `bridge_asleep` (exit code `3`) with the task ID to resume through `agentbridge wait`. The task remains queued/running. A wake signal cannot dismiss a modal dialog or unblock deadlocked user code.

The independent signal timer has no Windows dependency; its background/reload behavior has been tested on Windows with Unity 2022.3 and 6.4. If the private API is missing or fails, Windows falls back to a native message timer (`Wake timer: thread`); other platforms report `unsupported`. That fallback can wake the OS message queue but does not guarantee an Editor update. `agentbridge doctor` warns with `! interaction_throttled` when the Editor reports a throttling mode. Unity versions without an `EditorApplication.interactionMode` property report `Interaction mode: unknown`.

Only the interactive editor owns the queue. Batch mode and Asset Import Workers do not start the coordinator, test callbacks, scene recovery or wake timer. Workers share the project's Library but not the editor's SessionState; letting them recover records could otherwise mark a live task `interrupted_by_domain_reload`.

### C# Task Script

The script must follow this template:

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

public static class Task_20260226_143052
{
    public static async Task<string> Run()
    {
        // your code
        return "result description";
    }
}
```

The class name must match the file name — that name becomes the `TaskId`. `Run()` must return `Task<string>` (an overload taking a `CancellationToken` is also accepted and preferred when present) — any other signature is rejected before compilation. Bridge compiles the script in memory with Roslyn and invokes `Run()` on the main thread without blocking it, so you can freely `await` async APIs (thread pool, `Task.Delay`, Unity async operations); it writes the result only after the returned `Task` completes. Blocking constructs (`.Wait()`, `.GetAwaiter().GetResult()`, `.Result`, `Thread.Sleep`, unconditional `while(true)`/`for(;;)`) are rejected before execution — use `await` instead.

### Cleaning Up Tasks

Bridge cleans up on its own: while idle, it keeps only the last N tasks per `KeepCompletedCount` (default 10, configurable in `ProjectSettings/AgentBridge.json`), removing older journal entries together with their inbox files and `Artifacts/<id>/` directory. Nothing to clean up by hand.

## Scene Safety

No bridge path ever opens Unity's modal save dialog. A blocked Editor is a hung agent, so every task kind — `csharp`, `ui`, `compile`, `tests`, `sceneshot` — runs a scene preflight first. It inspects every open scene, including scenes that are open but unloaded, plus the current prefab stage.

Two settings in `ProjectSettings/AgentBridge.json`, both exposed in **Tools → Agent Bridge → Setup...**:

| Setting | Default | Meaning |
|---|---|---|
| `DirtyScenePolicy` | `Save` | `Save` silently saves a dirty scene that has a path, and a dirty prefab stage, before the task runs. `Block` ends the task as `runtime_error` before its payload executes and leaves the scene and the stage untouched. |
| `DirtyUntitledScenePolicy` | `Discard` | `Discard` closes a dirty untitled scene without saving. `Block` leaves it open and ends the task as `runtime_error`. |

A missing, empty or unknown policy value is read as the default.

An open-but-unloaded dirty scene always blocks the task regardless of policy: it cannot be saved, and closing someone's scene is not the bridge's call. Load and save it, or close it.

The preflight is a snapshot, and the Unity Test Framework runs its task list asynchronously — arbitrary Editor ticks pass between the preflight and its `SaveModiedSceneTask`. So the bridge also arms a watcher for the duration of the task and of the whole test run: whatever dirties a scene afterwards (a person working in the Editor, a domain reload, a project editor callback) is normalized on the next tick, and the task `Logs` record the scene path, the action taken and a trimmed call stack of the source. Inside an already started test run a scene with a path is saved even under `DirtyScenePolicy = Block` — Test Framework 1.1.33 has no way to cancel a run at that point, so the only alternative left would be the dialog.

`csharp` tasks are additionally denied the interactive Editor API at compile time: `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo` / `SaveModifiedScenesIfUserWantsTo`, `EditorApplication.EnterPlaymode` / `ExitPlaymode` / `Exit` / `ExecuteMenuItem`, assignments to `EditorApplication.isPlaying` and `isPaused`, `EditorUtility.DisplayDialog` / `DisplayDialogComplex` / `OpenFilePanel` / `OpenFolderPanel` / `SaveFilePanel` / `SaveFilePanelInProject`, `PrefabStageUtility.OpenPrefab`, `AssetDatabase.OpenAsset` and `TestRunnerApi.Execute`. Scene transitions go through `AgentBridge.AgentSceneManager`, tests through `agentbridge tests`, play mode through `agentbridge play`.

## Play Mode

An agent task that reaches play mode on its own hangs the bridge: the coordinator stops taking work while the Editor plays, and the task that started it is already gone. So the guardrail also rejects the cheap workarounds — `ExecuteMenuItem` and the string literals `"EnterPlaymode"`, `"ExitPlaymode"`, `"isPlaying"`, `"Edit/Play"` that reflection and menu paths need — and play mode gets its own commands instead. Reading `EditorApplication.isPlaying` stays allowed.

```bash
agentbridge play [--seconds N] --note <intent> --session <id>
agentbridge stopplay [--session <id>]
```

- `play` requires `--session`; that session owns the play mode. The result is `success` with `ReturnValue: "playing_until:<UTC>"`.
- `play` also requires `--note` — a short statement of what the session is for. A play session locks the Editor away from every other agent, so the neighbours get to see why it is busy, and an agent with nothing to write there has no reason to be in play mode. Both the CLI and the bridge reject a `play` without it. `--seconds` defaults to the editor setting, which is deliberately short (15 s); `compile`, `tests`, `sceneshot` with `"view": "scene"` and `uishot` answer most questions without play mode at all.
- Inside its own play session an agent may run only `csharp` and `sceneshot` — including `"view": "game"`, which captures the real Game View with its overlay UI. Every other kind is rejected with `kind not allowed during play session`.
- `stopplay` is an immediate control command: it bypasses the work queue and ownership, including an active PlayMode test. Success confirms exit from Play Mode; test cancellation and scene cleanup may still block subsequent work.
- The session ends on its own at the deadline, and a human pressing Stop ends it too (`stopped:external`). The Editor itself never exits a play mode a human started; play mode an agent slipped into without a session it does exit, and the culprit's journal record says so.
- Play mode that belongs to nobody is taken over by the client instead. The client submits an immediate `stopplay`, waits for it, and continues waiting for the original task — up to three takeovers per wait, after which it just waits. The takeover is announced on stderr (`editor is in play mode without an agent session; stopping it (stopplay #1)`); stdout still carries exactly one result, the task's own. Play mode owned by an agent session is never taken over this way — it keeps the `stopplay` rules above.
- During tests, `play` still waits for the editor; `stopplay` cancels the PlayMode test and requests exit immediately.
- Entering play mode does not steal your focus. Both bridge entries — `play` and `tests --mode PlayMode` — suppress the Editor's habit of activating the Game View, and the session runs with `Application.runInBackground`, so the game keeps ticking while Unity sits in the background. If the Editor still jumps to the front, the bridge hands focus back to the window that had it, at most twice per entry — click into Unity yourself and it stays yours. Windows only; elsewhere the suppression applies and the hand-back is a no-op.
- `agentbridge status` reports `IsPlaying`, `PlaySessionAgentId` and `PlaySessionDeadlineUtc`; in `--format human` an unowned play mode reads `Playing: yes (manual)`. `doctor` warns with `! editor_playing_manual`. If a wait does run out of its queue budget while the Editor plays manually, the `Status: queued` result carries `Reason: editor_playing_manual`.

Four settings in `ProjectSettings/AgentBridge.json`, all exposed in **Tools → Agent Bridge → Setup...**:

| Setting | Default | Meaning |
|---|---|---|
| `PlaySessionDefaultSeconds` | `15` | Session length when `--seconds` is omitted. |
| `PlaySessionMaxSeconds` | `600` | Upper bound `--seconds` is clamped to. |
| `AgentPlayGraceSeconds` | `5` | How long after a task finishes play mode still counts as agent-caused. |
| `PlayOwnerIdleSeconds` | `10` | Legacy setting; immediate `stopplay` does not wait for owner inactivity. |

## Multi-Agent Sessions

Several agents can drive the same Editor. Tasks still run strictly one at a time; what the scheduler decides is whose task runs next.

Pass `--session <id>` (1–64 characters of `A-Za-z0-9_-`) with every command to identify the session. A task without `--session` is its own one-shot session and behaves exactly as before. Optionally add `--note "<text>"` to explain what the session is doing.

- The session holding the lease runs its tasks back to back while nobody else is waiting.
- Once another session queues work, the holder keeps the editor for at most `ContentionSliceSeconds` and then the queue rotates — always on a task boundary, never mid-task. A holder with nothing queued rotates immediately, and a holder idle for longer than `LeaseIdleTimeoutSeconds` loses the lease on its own.
- A rotation carries the scene context: the outgoing session's scene setup and open prefab stage are saved, and the incoming session's are restored, so a session that comes back finds the scenes it left.
- Every result carries a `Contention` block — how many foreign sessions are waiting, for how long, and their `--note` texts. `--format human` prints it as `Contention: 2 waiting, oldest 47s`.
- `tests` and `compile` on an unchanged project skip the queue entirely: they are answered from the result cache or attach to a compatible run already in flight, without taking the lease or switching scene contexts. Only a run after real changes costs editor time; `--fresh` opts out of the cache.
- `agentbridge release --session <id>` hands the editor back early instead of waiting for the idle timeout. It answers `released` when the session held the lease, `not_holder` otherwise, and never interrupts another session's slice.
- `--wait` limits the entire client wait, including queueing. Exit 2 preserves the same task; resume with `agentbridge wait <TaskId>`. Status exposes `QueueBlockReason` and `QueueBlockedSinceUtc`; a live heartbeat does not imply that the queue is advancing. If the bridge dies while queued, the client exits 3 with `bridge_unavailable`.

Two settings in `ProjectSettings/AgentBridge.json`, both exposed in **Tools → Agent Bridge → Setup...**:

| Setting | Default | Meaning |
|---|---|---|
| `LeaseIdleTimeoutSeconds` | `120` | An idle holder with an empty queue loses the lease after this long. |
| `ContentionSliceSeconds` | `90` | How long a holder may keep working after another session starts waiting. |

Restarting the Editor drops the lease but keeps the saved session contexts; a domain reload changes nothing.

## Coordinating Several Agents

Bridge coordinates Unity Editor operations and writes to Unity inputs; agents manage the rest of their work independently. Analysis, reading, ordinary specs/TDDs, documentation, notes, reports, and independent checks outside Unity need no registration, edit grant, or window, even in the same repository. Keep them out of Bridge scopes. Resolve conflicts over those files through the agents' own workflow. External files do need coordination when Unity actually imports, compiles, or consumes them; writing through a shell does not change that boundary. While waiting for Unity, continue independent work. Release the editor before reporting or updating specs.

The session scheduler above serialises *commands*. It does not coordinate *writes to Unity inputs*, and it cannot tell whether a green test run describes the project you asked about. `coordination-v1` and `evidence-v1` add both, and only for projects that opt in: with no registered sessions every command behaves exactly as it did before.

Check first — the CLI and the package version their contracts separately:

```bash
agentbridge coord capabilities --format human
```

### Ready packages and input edits

Check for `coordination-batch-v1` and `coordination-edit-leases-v1` on both CLI and package. Register a scope containing only Unity input paths. Registration declares intent and does not reserve files: overlapping registrations, including idle sessions waiting for tests, are allowed. Use `edit-begin` / `edit-end` around bounded writes to those inputs; independent work needs no Bridge permission.

Submit all executable work together:
```bash
agentbridge coord register --session AB_A --spec my_tdd --repo D:/repo --scope scope.json --owner host/task-17
agentbridge coord submit --session AB_A --request <uuid> --kind editor --plan plan.json --seconds 120
```

Example plan:
```json
{ "Steps": [
  { "Id": "Build", "Kind": "csharp", "PayloadFile": "Task_Build.cs" },
  { "Id": "Layout", "Kind": "ui", "PayloadFile": "screen.ui.json" }
] }
```

PayloadFile is relative to the plan file. Submission freezes UTF-8 content and its hash in the store; changing the original file cannot change queued work. Inline Payload plus PayloadName is also supported. For C#, PayloadName is the class name; file-based submission derives it from the source filename. A hash alone is not executable work and is refused before entering FIFO. Limits: 64 steps, 4 MiB total payload, 15–600 seconds of editor time (default 120).

`coord request` is an alias for this complete submission. It no longer reserves an empty window. The response contains RequestId and deterministic TaskIds for every planned step, including steps that may later be skipped. Unity waits for input writers and editor recovery, executes the steps in order, stops the package on its first failed step, and closes the window automatically. No subsequent agent dispatch, polling, or `finish` is needed. A continuation requiring agent reasoning is a new package.

Use `coord status --session AB_A --request <uuid>` or event-driven `coord wait --session AB_A --request <uuid> --after <revision> --wait 30` to observe progress. State=closed with Reason=completed means the package succeeded; failed:<step>:<reason>, expired, and interruption reasons are not success. Read an executed task with `agentbridge wait <TaskId>`. Repeating a submission with the same uuid and frozen content returns the existing request, including after completion, without repeating its effects. A different payload with the same uuid is refused.

A validation plan supports compile, tests, sceneshot; an editor plan supports csharp, ui, sceneshot, compile. Test steps declare Mode (EditMode or PlayMode) and a nonempty Tests, Assemblies or Categories filter. For example: `{ "Steps": [{ "Id": "V1", "Kind": "tests", "Mode": "EditMode", "Tests": ["MyTests"] }] }`. Optional ArtifactRoots and FixtureRoots retain their evidence-v1 meaning.

Scope paths are repo-relative, with forward slashes, a trailing slash for directories and no globs: `{ "Paths": ["UnityProject/Assets/Game/Core/"] }`. Edit grants last 15–300 seconds (default 120), starting when granted. Overlapping edits queue in ticket order; disjoint edits may run together. A waiting reply is not permission to write: wait for a granted request and its token. While a ready package or an overlapping writer waits, renewal returns `pause_requested`: finish the current bounded writes and call `edit-end`. Scope changes are allowed only without a grant or pending request.

At the deadline the edit grant closes automatically and the queue advances. A late `renew` returns `stale_token`; a late `edit-end` reports `already_closed`. Do not leave background writers beyond the deadline. After a pause or expiry, request a new edit grant with a new UUID, reread the current files, and adapt changes before writing: another agent may have edited the same files. Close registration with `coord leave` when its Unity work is done; forgetting to leave no longer reserves files.

Upgrade the Unity package and all CLI clients together. The package advertises `coordination-edit-leases-v1`; the new CLI refuses mutations until it sees that capability. The first mutation migrates state schema 1 to 2 atomically, preserving registrations and running windows and releasing old orphaned edits. Old CLI builds refuse schema 2 instead of scheduling overlapping writers under the former assumptions. No manual deletion or abandonment of idle registrations is needed.

Upgrading rejects old waiting permission-only requests with batch_required. Already running old windows drain safely; their tasks are not interrupted merely to migrate. Ordinary task commands without tokens remain supported and wait while a window is occupied. Older packages without the batch capability require the old manual request/wait/dispatch/finish workflow.

Exit codes for coord: 0 operation accepted/read succeeded, 1 refusal or conflict, 2 observation wait expired, 3 bad usage or unsupported package/store. Read the package outcome and task results; a successful submission is not successful execution.

Known Unity compiler errors set `input_repair_pending`. New validation packages remain queued without holding the editor; input edits can pass older blocked validations. An idle validation window closes automatically, while a running task retains ownership until it stops and recovery finishes. The owner of a compiler error requests edit-begin, fixes the input, and calls edit-end without asking neighboring agents to cancel their runs. A completed edit or a new successful compiler cycle restores validation eligibility. Temporary C# task errors do not put the Unity project into this state. If the owner already has a waiting validation request, it can cancel that own request before requesting its edit grant.

### Recovery

Five different kinds of "it stopped", and they are not interchangeable:

| What happened | What the bridge does |
|---|---|
| The client process went away | Nothing. The request and the task live under their own ids; reconnect with `coord status --request <uuid>` or `agentbridge wait <TaskId>`. |
| A `wait` expired | Nothing. It never cancels a request and never moves the revision. |
| A domain reload from your own compile or PlayMode | The window, its token and its remaining steps survive. |
| The Editor process restarted | Registrations and scopes survive; edit grants retain their original deadlines. Windows become `interrupted`; the recorded tasks are reported failed and the window closes. Ask for a new one. |
| A writer disappeared with a live grant | The grant closes at its deadline. The next eligible writer or window can proceed without owner cleanup. A running Unity task still drains through its normal completion/cancellation and recovery. |

`coord abandon --target-session S --reason "<text>"` remains an explicit emergency override for a still-live session, not routine expiry cleanup. It requires an explicit human decision after that session's writers are confirmed stopped, refuses while that session has a running Unity task, closes only that session's rights, and bumps only its generation. Idle registrations and expired edit grants need no such override.

State lives in `Library/AgentBridge/Coordination/` behind one persistent `transaction.lock`. `coordination-v1` supports an ordinary local physical tree only: UNC paths, network drives and symlinked project roots are refused rather than falsely declared protected. The lock file is never deleted to "recover"; a damaged `state.json` is reported as `coordination_corrupt`, and a missing state next to a live marker as `coordination_recovery_required`.

The coordinator hands out rights. It is **not** a filesystem sandbox: a tool that ignores the protocol can still write to disk. The defence against that is invalid evidence, below — not a promise to stop every OS write.

### Evidence

Every `tests` and `compile` result now carries an `Evidence` block, and `--format human` prints it as `Evidence: valid|stale|unknown`.

Before a validation the bridge settles the import, hashes the *content* of every input off the main thread, installs file observers, and hashes again to close the gap between looking and watching. The inputs are `Assets/`, `Packages/`, `ProjectSettings/` and every resolved local package, including `.meta` files; `Library/`, `Temp/`, `Logs/`, `obj/` and declared fixture roots that were provably empty are excluded. Unity and package versions, the active build target and the test filter are part of the claim.

Dynamic TMP font asset files are excluded from input hashing and file observation, including the compile-cycle observer: Unity's saved glyph tables and embedded atlas textures do not invalidate results. Fonts are identified by `TMP_FontAsset` type and non-static atlas population mode, including fonts in local packages. Static fonts, source TTF/OTF files, `.meta` files and separate texture/material assets remain tracked. This excludes the entire dynamic font asset, so manual edits inside it also do not invalidate the cache; use `tests --fresh` after such edits. Switching a font to Static restores tracking on the next input capture. Run `powershell -File scripts/verify-dynamic-tmp.ps1` against the idle test editor to check actual asset saves, hashes and observer events.

File observers are shared, not per-run. Unity's Mono picks a polling `FileSystemWatcher` that walks the whole root before it reports anything — on a large project that is hundreds of milliseconds of editor main thread per install. The bridge therefore keeps one observer per input root for the whole editor, arms it on a worker thread, and lets every run, cache lookup and attach check open its own window over it with its own exclusions and its own event count. A root stays armed for half a minute after the last window closes, so the once-a-second cache lookup normally costs nothing at all. Nothing about the verdicts changes: the observation window still opens in exactly the same places it did before.

The compile fingerprint hashes source contents, source metadata, assembly inputs, settings, resolved local packages and compilation context. Timestamps alone do not invalidate it; changed bytes with preserved size and timestamp do. The broader evidence digest additionally covers gameplay assets and observes changes throughout the validation.

- **stale** — an input really changed during the run, or was changed and changed back. The NUnit numbers stay in the record as diagnostics, the terminal status becomes `stale_input` (exit 1), nothing is promoted to the cache, and every attached request gets the same verdict instead of being sent around the queue again.
- **unknown** — the inputs could not be fully hashed or observed: an unreachable package root, an overflowed observer. Outside a coordinated validation window this is advisory and does not turn a green run red. Inside one it is not an acceptance: the status becomes `evidence_unavailable` (exit 1).
- If the project will not hold still *before* the run, the expensive run is refused up front with `evidence_unavailable` rather than producing a result nobody can interpret.

A run has a second witness that needs no thread: a **stat manifest** of the same inputs — path, length and last write time — taken on a worker while the run is prepared, written to `Library/AgentBridge/evidence-manifest-<TaskId>-<attempt>.txt`, and compared against the same walk when the run is over. It exists because the observer cannot cover a PlayMode run: it dies with the domain, and Unity's polling watcher never delivers the last seconds before the reload either. Any difference the manifest finds is an `input_changes` event exactly like an observed one — the telemetry line carries `Source` so the two witnesses stay distinguishable — and it makes the result `stale`. A manifest that is missing or unreadable makes the result `unknown`, never `valid`: a witness that is gone is not a witness that saw nothing. A successful run through a reload says so in its reason: `observer reinstalled after 1 domain reload(s); the stat manifest covers the gap; both input digests match`. Manifests belong to the run in flight and are deleted when it ends, including when it is aborted.

One known limit remains, reported honestly rather than papered over:

- A `compile` that actually has new sources to import is reloaded by its own `AssetDatabase.Refresh` before the snapshot finishes, so it reports `Evidence: unknown (this result was produced without an input snapshot)`. The compile result itself is unaffected, and `compile` on an already-imported project reports `valid`. Acceptance that needs an input digest should rely on a `tests` step, which takes its snapshot with no refresh in front of it.

The bridge's own scratch is never blamed on anybody: the temporary `Assets/InitTestScene*.unity` that the Unity Test Framework creates for a PlayMode run, and that the bridge deletes afterwards, is excluded from both the digest and the observers. An asset that merely shares that prefix is an ordinary input.

### Cached test sets

`test-cache-v2` keeps up to 32 completed sets in `Library/AgentBridge/TestCacheV2/`, evicting the least recently used. Each set stores its own input digest, mode, filter, per-test results and artifacts, so A → B → A on unchanged inputs costs two real runs and one cache hit instead of three runs.

A hit needs an exact input digest match, the same mode, one single set that covers the whole request, a non-empty selection, `Validity = valid`, every mandatory artifact still on disk — a screenshot that was deleted is never handed back as visual acceptance — and a clean stat manifest over the lookup itself, so an input that was edited and put back while the decision was being made is not served as a hit. Results from different digests are never merged. `--fresh` skips both the cache and attaching. The old single-file-per-mode cache is still readable as legacy diagnostics and is never relabelled as evidence.

## Custom Project APIs

If the project has custom APIs (libraries, tools, builders), you can describe them for Bridge so that Claude uses them when generating scripts. Create a `UNITYAGENT.md` file next to the library code.

When executing a task, the skill recursively searches for all `UNITYAGENT.md` files in the project and reads them. If the described API is suitable for the task, Claude will use it instead of the standard Unity Editor API.

File format:

```markdown
# API Name

Brief description: what it does and when to use it.

## When to Use

Description of tasks this API applies to.

## Namespace / Using

Which using directives to add.

## Main Classes and Methods

Public API with examples.

## Examples

Ready-made examples for typical scenarios.
```

Detailed template with recommendations: `Docs/UNITYAGENT-template.md`

No separate documentation is needed for the standard Unity Editor API — Claude knows it out of the box.

## Declarative UI Tasks

Besides C# scripts, Bridge accepts a second task kind for uGUI layout: `agentbridge ui <path-to-ui-json>`. Bridge applies the file to a prefab **directly**, without compiling C# or reloading the domain, so layout iterations take seconds. The task id is the file name without the `.ui.json` suffix. Scope is uGUI + TMP only — UI Toolkit is not supported.

One task targets one prefab and runs a list of actions:

```json
{
    "prefab": "Assets/Resources/Prefabs/UI/MyScreen.prefab",
    "actions": [
        { "action": "apply", "target": "Popup", "node": {
            "rect": { "anchorMin": [0.5, 0.5], "anchorMax": [0.5, 0.5], "pos": [0, 0], "size": [600, 400] },
            "components": [ { "type": "Image", "sprite": "Assets/Sprites/UI/PopUp.png", "imageType": "Sliced", "color": "#FF005A" } ],
            "children": [
                { "name": "Title", "rect": { "anchorMin": [0, 1], "anchorMax": [1, 1], "pos": [0, -40], "size": [0, 60] },
                    "components": [ { "type": "Text", "text": "TITLE", "size": 42, "align": "Center" } ] }
            ]
        } },
        { "action": "uishot", "outline": ["Popup"] }
    ]
}
```

- `apply` — create/update a node by path; specified properties are set, unspecified are left alone, `null` clears; `children` are synced by name (extra children are never removed).
- `delete` — remove a node by path.
- `dump` — write `Library/AgentBridge/Artifacts/<id>/uidump.json`: the whole tree with anchors, sizes, `screenRect` in reference pixels, and object references of custom components.
- `uishot` — render the prefab offscreen to `Library/AgentBridge/Artifacts/<id>/shot.png` (1920×1080 by default) plus a `.rects.json` with every node's screen rect; `outline` draws colored frames for the listed paths. Optional `output` is a PNG file name only, never a path. Absolute paths and directory segments are rejected, so every transient UI artifact remains owned by its task.

Order within a task: all `apply`/`delete` run first over the loaded prefab contents, then a single save, then `dump`/`uishot` over the saved asset. If the prefab does not exist and there is an `apply`, it is created (root `RectTransform` stretched 0..1). Any error (bad JSON, missing prefab/sprite/type/path) yields `runtime_error` and leaves the prefab unchanged.

Object references — `ref`, a button's `targetGraphic` and its `wire` list — are collected while nodes are applied and resolved in a single pass after the last action, so a reference may point at a node that is created later in the same task. A component type name may be written short (`"Button"`, `"Text"`, `"UI.MyView"`): resolution only considers types deriving from `Component`, and an ambiguous short name fails the task with the list of full names to pick from.

## Scene Screenshots

`agentbridge sceneshot <path-to-sceneshot-json>` photographs the currently open scene from the angles listed in the file — no compilation, no domain reload. The task id is the file name without the `.sceneshot.json` suffix. Each shot opens a temporary Scene View window of the requested size, poses its camera and renders that view into a texture — not a screen grab, so an occluded, unfocused or fully minimized Editor produces the same image. That temporary window is shown without activation, so taking a screenshot does not pull Unity in front of whatever you are working in; `"view": "game"` reuses an existing Game View as-is and only creates an unfocused one when the project has none.

```json
{
    "shots": [
        { "name": "hero", "width": 1280, "height": 720,
            "frame": { "target": "Level/Hero", "margin": 1.1, "rotation": [30, 45, 0] } },
        { "name": "top", "gizmos": false,
            "pose": { "pivot": [0, 0, 0], "rotation": [90, 0, 0], "size": 40, "orthographic": true } }
    ]
}
```

- `view` is `"scene"` by default. `"game"` captures the real Game View instead, overlay UI included; it works only during an `agentbridge play` session and ignores `width`/`height`, `pose` and `frame` — the game's own camera and the Game View resolution decide the frame. Outside play mode the task ends as `runtime_error`.
- Exactly one of `frame` or `pose` per scene shot. `frame` centers the view on a scene object found by name or by a `Root/Child` path, like pressing F on it; `pose` sets the Scene View camera explicitly.
- `width`/`height` default to 1280×720 and top out at 1920×1080. The Scene View window is a real OS window, so a request larger than the desktop is scaled down by one factor on both axes to keep the framing; `Logs` notes the reduction and `ReturnValue` carries the real size.
- `gizmos` defaults to `true` and `grid` to `false`; `name` becomes the PNG file name.
- PNGs land in `Library/AgentBridge/Artifacts/<id>/` and their absolute paths come back in `Artifacts`.
- Only the open scene is captured and the shot task itself changes nothing, but the common scene preflight runs before it: under `DirtyScenePolicy = Save` an unsaved scene is silently saved first, under `Block` the task ends as `runtime_error`. To photograph another scene, open it with a `csharp` task first. A failing shot ends the whole task with `runtime_error`; already captured PNGs stay in `Artifacts`.

### Layout conventions (`UNITYAGENT-UI.md`)

Before laying out UI, the `unity-ui` skill recursively searches the project for a `UNITYAGENT-UI.md` file describing your layout conventions — reference resolution, palette, fonts, art paths, prefab paths, and custom view components. Create one so Claude uses your real colors, fonts and assets instead of guessing. Template with recommendations: `Docs/UNITYAGENT-UI-template.md`.

## Working Directory

Claude writes its own task files (`Task_XXX.cs`, `Task_XXX.ui.json`, `Task_XXX.sceneshot.json`) to `<project>/Temp/AgentBridge/` — the absolute path is reported as `ScratchDir` by `agentbridge status`, and the CLI creates the folder itself. Unity never imports `Temp/`, so tasks never trigger an asset import, a recompile, or stray `.meta` files, and the Editor wipes the folder on start and shutdown — nothing to clean up. Never keep task files under `Assets/`; `agentbridge csharp|ui|sceneshot` prints a warning to stderr when the payload lives there.

Bridge's own transport lives separately:

```
Library/AgentBridge/
├── status.json                 ← protocol/package/project/Editor status and capabilities
├── heartbeat                   ← liveness marker, updated every ~2s
├── project-id                  ← host-independent project identity, mirrored in status.json
├── cli/
│   └── agentbridge             ← Linux build for agents running in a sandbox
├── Inbox/
│   ├── Task_XXX.task.json      ← task request (Id, Kind, PayloadFile, ...)
│   ├── Task_XXX.cs             ← payload for a csharp task
│   └── Task_XXX.ui.json        ← payload for a ui task
├── Journal/
│   └── Task_XXX.json           ← single result record per task (TaskRecord)
└── Artifacts/
    └── <id>/                   ← removed together with the owning journal entry
        ├── uidump.json         ← UI dump output
        ├── shot.png            ← default UI screenshot output
        └── shot.png.rects.json ← screen rects for the screenshot
```

Telemetry is deliberately not in there: it lives in `<project>/Logs/` so that deleting `Library/` to
fix something does not delete the record of what went wrong.

## Telemetry

Bridge writes down what happened to every task, so questions like "why did this wait four minutes",
"who is holding the editor", "did the editor fall asleep again" and "which timeout fired" can be
answered after the fact instead of guessed at.

Two writers, two files, one line of JSON per event:

```
Logs/AgentBridge-editor-YYYYMMDD.jsonl   ← queue, lease, timeouts, play sessions, tick gaps
Logs/AgentBridge-client-YYYYMMDD.jsonl   ← what the agent actually waited for, and its exit code
```

Both processes run on the same machine and the same clock, so the two halves of a task join on its
id. Every line starts with the same envelope — `T` (unix ms UTC), `W` (`editor` or `client`), `E`
(event name), `S` (agent session, empty if none), `Id` (task id, empty if the event is not about a
task) — followed by the fields of that event.

| Writer | `E` | Fields |
|---|---|---|
| editor | `bridge_start` | `Package`, `Unity`, `Wake`, `Interaction`, `Pid` |
| editor | `tick_gap` | `GapMs`, `HasWork`, `Focused` — the editor missed a tick for ≥ 2 s |
| editor | `task_start` | `Kind`, `WaitedMs`, `QueueDepth`, `Rotated`, `Note` |
| editor | `task_finish` | `Kind`, `Status`, `TotalMs`, `Cached`, `Waiting`, `OldestWaitS` |
| editor | `lease_grant` | `Reason` (`first`/`rotation`), `Prev` |
| editor | `lease_release` | `Reason` (`idle_timeout`/`release_cmd`), `HeldMs` |
| editor | `play_open` | `RequestedS` |
| editor | `play_close` | `Reason`, `ActualMs` |
| editor | `watchdog` | `What` (`task_timeout`/`compile_no_reload`/`play_enter`), `Kind`, `LimitS` |
| client | `cli_submit` | `Cmd`, `Note` |
| client | `cli_wake` | `Action` (`post`/`focus`), `AgeMs`, `Sent` (native call succeeded; not proof of an Editor tick) |
| client | `cli_exit` | `Cmd`, `Code`, `Status`, `QueuedMs`, `RunningMs`, `Posts`, `Focuses` |

There is no aggregate command on purpose — a fixed report would hide exactly the unexpected thing
the log exists to reveal. Read the raw JSONL:

```bash
cd <project>/Logs

grep '"tick_gap"' AgentBridge-editor-*.jsonl      # did the editor fall asleep, and when
grep '"watchdog"' AgentBridge-editor-*.jsonl      # every timeout that fired
grep '"cli_exit"'  AgentBridge-client-*.jsonl     # how each wait ended for the agent

# the longest queue waits
cat AgentBridge-editor-*.jsonl | grep '"task_start"' | python3 -c "
import sys, json
rows = [json.loads(line) for line in sys.stdin]
for event in sorted(rows, key=lambda r: -r['WaitedMs'])[:20]:
    print(event['WaitedMs'], event['Kind'], event['S'], event['Note'])
"
```

Two settings in `ProjectSettings/AgentBridge.json`:

| Setting | Default | Meaning |
|---|---|---|
| `TelemetryEnabled` | `true` | Turns both writers off. The editor reads it per event; the client reads `TelemetryEnabled` from `status.json`, so it follows after a domain reload. |
| `TelemetryKeepDays` | `14` | Files older than this are deleted on the first event of a new day. |

Telemetry never fails a task: a write that cannot happen is dropped silently.

## Limitations

- Works in Unity Editor. Play Mode is reachable only through an owned play session (`agentbridge play`), and only `csharp` and `sceneshot` run inside one; `tests --mode PlayMode` enters Play Mode on its own and is unaffected. See [Play Mode](#play-mode).
- Tasks are processed strictly one at a time — Bridge does not start a new task while one is in flight. Order is oldest first within an agent session; between sessions the scheduler rotates the editor on task boundaries (see [Multi-Agent Sessions](#multi-agent-sessions))
- `Run()` is invoked on Unity's main thread; awaited continuations resume there too. Use cancellation-aware asynchronous code. C# tasks and test runs have no global timeout. After 300 seconds another agent may request cancellation; the queue remains held until the executor stops and test scenes are restored. A blocked main thread or uncooperative script can prevent cancellation; `cancel_blocked` never means the editor is free.
- A running task can be aborted via **Tools → Agent Bridge → Cancel Running Task**
- `csharp` tasks compile against whatever assemblies are already loaded in the domain — they cannot reference project code that has compilation errors, since the broken assembly itself would never have loaded. Use a `compile` task first to confirm the project builds.
- Background execution uses a private Unity API with a native Windows fallback. macOS/Linux behavior has not been runtime-validated, and a blocked main thread still requires intervention. See [Running While the Editor Is in the Background](#running-while-the-editor-is-in-the-background).
- Roslyn ships inside the package (`Roslyn~/`), so no download and no network access are required; third-party licenses are in `Roslyn~/THIRD-PARTY-NOTICES.md`.

## Releasing

The three components version independently:

| Component | Version source |
|---|---|
| AgentBridge CLI | `<Version>` in `AgentBridgeCli/AgentBridgeCli.csproj` |
| Unity package | `version` in `AgentBridgeUnity/Packages/com.elmortem.agentbridge/package.json` |
| Agent plugin | `version` in `unity-bridge-plugin/.claude-plugin/plugin.json` |

Every changed component must increase its own version. This is fail-closed in `scripts/build-plugin.ps1` and in the **Release Contract** GitHub Action: a package, plugin, or CLI change without a greater corresponding version fails validation.

Build the distributable plugin only with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1
```

The same script validates every `skills/<name>/SKILL.md` frontmatter before packaging: `description` at most 1024 characters, `name` at most 64 characters, lowercase kebab-case and equal to the skill directory, single-line `key: value` fields only. An oversized description produces a valid ZIP that the agent host still refuses to load, so this check is fail-closed too and prints `frontmatter_validation=PASS` with the actual lengths.

Do not use `Compress-Archive` for this artifact. On Windows it can store backslashes in ZIP central-directory entry names and consumers then report `Zip file contains path with invalid characters`. The canonical script writes explicit forward-slash names, rejects `\`, absolute paths, `..`, duplicates and Windows-invalid characters, then compares every archived file hash with its source. A successful build ends with `invalid_entries=0` and `zip_validation=PASS`.

Before packaging, the script normalizes CRLF to LF in the plugin's UTF-8 Markdown and JSON sources, matching `.gitattributes` and fresh Git checkouts. `-ValidateOnly` never rewrites files: it rejects CRLF sources with an instruction to rebuild. Archive validation remains a strict byte comparison, including actual text changes.

Only the CLI has a publishing pipeline. Bumping `<Version>` in the csproj and pushing is the entire release procedure: the workflow runs the tests, sees that no `agentbridge-v<version>` release exists yet, creates the tag and release at that commit, then builds and attaches the six self-contained binaries with checksums. Pushing without a version bump only runs the tests — the release step is skipped because the tag already exists, so no tags are created by hand.

`workflow_dispatch` re-packages an existing tag; use it to repair a release whose assets failed to upload.

The Unity package is consumed straight from the git URL, so it needs no publishing step — pushing the branch is enough. The plugin ZIP remains tracked in the repository; the Release Contract action validates the committed archive, rebuilds it independently, and uploads it as a workflow artifact. It does not attach the plugin to the CLI GitHub Release.

Cache lookup runs independently of the work queue, including cancellation and scene recovery. Requests without a window token can reuse cache while another window owns the editor. Explicit coordinated steps retain validation and accounting. Fresh requests, cache misses and changed inputs stay queued. Lookup is deferred during compilation/import. Source reuse now hashes file contents as well as metadata; test input digests are rechecked before serving and kept separate per request/mode.

A lookup pays for evidence only when it has something to serve. The cheap source fingerprint names the candidate entries first; with no candidate the observers are never installed and no input digest is taken at all, so a queue full of waiting tasks does not keep a file watcher walking the project. Once a candidate exists, both witnesses open once per scan — the observer window and a stat manifest of the inputs — and a served `tests` result requires the manifest to agree that nothing moved between the two digest reads, not just that the digests match. A miss is remembered against the source fingerprint and the candidate set it was decided under: while both hold, the digest is recomputed at most once every ten seconds instead of once per scan, and an edit to either is rechecked on the next scan. Untracked inputs, such as a reverted texture, are covered by that ten-second backstop.

### Task queue window

Open **Tools → Agent Bridge → Task Queue** to see active, canceling, attached, and waiting tasks. The list refreshes twice per second. Drag a waiting task by its handle onto another row to place it before that task, or into the empty area below the list to move it last. **Automatic order** restores normal session scheduling. Manual priority survives assembly reloads within the editor session; coordination locks, cache checks and Play Mode eligibility still apply.

**Cancel** removes a waiting task by recording a terminal `canceled` result, leaving its request intact for the waiting CLI. Running C# and tests stop cooperatively; attached requests can be canceled without stopping the shared test run. During editor phases that cannot be stopped the window reports that limitation. A blocked editor main thread cannot process window input. Human cancellation has the same authority as the existing Cancel Running Task menu.

The queue window also shows **Ready packages and input edits** in ticket order. A ready package already contains every step and its frozen payload; individual Inbox entries are materialized by the editor just before execution. Rows show the ready step count and automatic execution. Waiting requests have their own Cancel button; granted windows and coordination ticket order are not changed by task drag-and-drop. The header identifies the Unity project and last refresh time. **Recent results** keeps up to 20 results from the last 10 minutes, so quick completions and rejections do not vanish between refreshes. Snapshot reads never admit or reject work; individual unreadable files produce a warning while other rows keep updating.

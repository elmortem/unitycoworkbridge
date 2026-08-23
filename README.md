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
- **`compile`** — forces Unity to compile the project and returns the resulting errors, surviving the domain reload this triggers. A repeat with unchanged sources is answered from cache instantly — the result carries `Cached: true` and the `SourceTaskId` of the original run; `--fresh` forces a real compilation.
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
```

Every command also accepts `--session <id>` and `--note <text>`, which identify the agent session behind the task; see [Multi-Agent Sessions](#multi-agent-sessions).

Exit codes: `0` success, `1` a terminal task failure including `test_failure`, `2` client wait exhausted (the task is still running — retry with `agentbridge wait <TaskId>`), `3` project/bridge unavailable, protocol mismatch, or bad usage.

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

`agentbridge status` validates the project path, package presence, Editor PID, heartbeat freshness and protocol version. `agentbridge doctor` additionally reports the CLI path/version, Unity/package versions, Roslyn readiness, capabilities and active task.

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
- `play` also requires `--note` — a short statement of what the session is for. A play session locks the Editor away from every other agent, so the neighbours get to see why it is busy, and an agent with nothing to write there has no reason to be in play mode. Both the CLI and the bridge reject a `play` without it. `--seconds` defaults to the editor setting, which is deliberately short (30 s); `compile`, `tests`, `sceneshot` with `"view": "scene"` and `uishot` answer most questions without play mode at all.
- Inside its own play session an agent may run only `csharp` and `sceneshot` — including `"view": "game"`, which captures the real Game View with its overlay UI. Every other kind is rejected with `kind not allowed during play session`.
- A foreign play session cannot be stopped: `stopplay` answers `rejected` with `play_session_held_by:<id>`. Play mode left behind without a session — a stuck task or a manual start — can be stopped by any agent. Outside play mode `stopplay` is a no-op returning `not_playing`.
- The session ends on its own at the deadline, and a human pressing Stop ends it too (`stopped:external`). Play mode a human started is never exited automatically; play mode an agent slipped into without a session is, and the culprit's journal record says so.
- While a test run is in flight both commands are rejected with `tests are running`: PlayMode test semantics are untouched.
- Entering play mode does not steal your focus. Both bridge entries — `play` and `tests --mode PlayMode` — suppress the Editor's habit of activating the Game View, and the session runs with `Application.runInBackground`, so the game keeps ticking while Unity sits in the background. If the Editor still jumps to the front, the bridge hands focus back to the window that had it, at most twice per entry — click into Unity yourself and it stays yours. Windows only; elsewhere the suppression applies and the hand-back is a no-op.
- `agentbridge status` reports `IsPlaying`, `PlaySessionAgentId` and `PlaySessionDeadlineUtc`.

Three settings in `ProjectSettings/AgentBridge.json`, all exposed in **Tools → Agent Bridge → Setup...**:

| Setting | Default | Meaning |
|---|---|---|
| `PlaySessionDefaultSeconds` | `30` | Session length when `--seconds` is omitted. |
| `PlaySessionMaxSeconds` | `600` | Upper bound `--seconds` is clamped to. |
| `AgentPlayGraceSeconds` | `5` | How long after a task finishes play mode still counts as agent-caused. |

## Multi-Agent Sessions

Several agents can drive the same Editor. Tasks still run strictly one at a time; what the scheduler decides is whose task runs next.

Pass `--session <id>` (1–64 characters of `A-Za-z0-9_-`) with every command to identify the session. A task without `--session` is its own one-shot session and behaves exactly as before. Optionally add `--note "<text>"` to explain what the session is doing.

- The session holding the lease runs its tasks back to back while nobody else is waiting.
- Once another session queues work, the holder keeps the editor for at most `ContentionSliceSeconds` and then the queue rotates — always on a task boundary, never mid-task. A holder with nothing queued rotates immediately, and a holder idle for longer than `LeaseIdleTimeoutSeconds` loses the lease on its own.
- A rotation carries the scene context: the outgoing session's scene setup and open prefab stage are saved, and the incoming session's are restored, so a session that comes back finds the scenes it left.
- Every result carries a `Contention` block — how many foreign sessions are waiting, for how long, and their `--note` texts. `--format human` prints it as `Contention: 2 waiting, oldest 47s`.
- `tests` and `compile` on an unchanged project skip the queue entirely: they are answered from the result cache or attach to a compatible run already in flight, without taking the lease or switching scene contexts. Only a run after real changes costs editor time; `--fresh` opts out of the cache.
- `agentbridge release --session <id>` hands the editor back early instead of waiting for the idle timeout. It answers `released` when the session held the lease, `not_holder` otherwise, and never interrupts another session's slice.
- While a task waits behind another session, the client waits in the queue and reports `queued <n>s, position <p>/<total>, holder <id>` on stderr; `--wait` starts counting only when the task actually starts in Unity, with a hard queue ceiling of 3600 seconds. If the bridge dies while the task is queued, the client exits 3 with `bridge_unavailable`.

Two settings in `ProjectSettings/AgentBridge.json`, both exposed in **Tools → Agent Bridge → Setup...**:

| Setting | Default | Meaning |
|---|---|---|
| `LeaseIdleTimeoutSeconds` | `120` | An idle holder with an empty queue loses the lease after this long. |
| `ContentionSliceSeconds` | `90` | How long a holder may keep working after another session starts waiting. |

Restarting the Editor drops the lease but keeps the saved session contexts; a domain reload changes nothing.

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

## Limitations

- Works in Unity Editor. Play Mode is reachable only through an owned play session (`agentbridge play`), and only `csharp` and `sceneshot` run inside one; `tests --mode PlayMode` enters Play Mode on its own and is unaffected. See [Play Mode](#play-mode).
- Tasks are processed strictly one at a time — Bridge does not start a new task while one is in flight. Order is oldest first within an agent session; between sessions the scheduler rotates the editor on task boundaries (see [Multi-Agent Sessions](#multi-agent-sessions))
- `Run()` is invoked on Unity's main thread; awaited continuations resume there too, so heavy synchronous work still blocks the Editor — offload it via `await Task.Run(...)`. Bridge caps a task at `TaskTimeoutSeconds` (default 300, configurable in `ProjectSettings/AgentBridge.json`); on timeout it writes `Status: "timeout"` and unblocks the queue
- A running task can be aborted via **Tools → Agent Bridge → Cancel Running Task**
- `csharp` tasks compile against whatever assemblies are already loaded in the domain — they cannot reference project code that has compilation errors, since the broken assembly itself would never have loaded. Use a `compile` task first to confirm the project builds.
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

Only the CLI has a publishing pipeline. Bumping `<Version>` in the csproj and pushing is the entire release procedure: the workflow runs the tests, sees that no `agentbridge-v<version>` release exists yet, creates the tag and release at that commit, then builds and attaches the six self-contained binaries with checksums. Pushing without a version bump only runs the tests — the release step is skipped because the tag already exists, so no tags are created by hand.

`workflow_dispatch` re-packages an existing tag; use it to repair a release whose assets failed to upload.

The Unity package is consumed straight from the git URL, so it needs no publishing step — pushing the branch is enough. The plugin ZIP remains tracked in the repository; the Release Contract action validates the committed archive, rebuilds it independently, and uploads it as a workflow artifact. It does not attach the plugin to the CLI GitHub Release.

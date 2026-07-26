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

There are four task kinds, all created via the CLI and processed sequentially, one at a time:

- **`csharp`** — a `.cs` script; Bridge compiles it in memory with Roslyn and runs its `Run()` method on the main thread.
- **`ui`** — a `.ui.json` file; Bridge applies it to a prefab directly, without compilation.
- **`compile`** — forces Unity to compile the project and returns the resulting errors, surviving the domain reload this triggers.
- **`tests`** — runs EditMode or PlayMode tests and returns pass/fail counts and failure details in the same result record, surviving Play Mode's domain reload.

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

Open a new terminal or restart the agent application, then verify:

```bash
agentbridge --version
```

If a GUI agent has not inherited the updated `PATH`, use the stable per-user install path directly: `%LOCALAPPDATA%\AgentBridge\bin\agentbridge.exe` on Windows or `$HOME/.local/bin/agentbridge` on macOS/Linux. The CLI is never discovered inside Unity's hashed `Library/PackageCache` path.

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

One cross-platform command creates a task, waits for it, and prints the result JSON to stdout. Run it from the Unity project root or any subdirectory. Outside the project, pass `--project <path>`.

```bash
agentbridge csharp /tmp/Task_20260226_143052.cs
agentbridge compile
agentbridge tests --mode EditMode --assembly MyGame.Tests
agentbridge status
agentbridge doctor --format human
```

Exit codes: `0` success, `1` a terminal task failure including `test_failure`, `2` client wait exhausted (the task is still running — retry with `agentbridge wait <TaskId>`), `3` project/bridge unavailable, protocol mismatch, or bad usage.

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
        { "action": "shot", "outline": ["Popup"] }
    ]
}
```

- `apply` — create/update a node by path; specified properties are set, unspecified are left alone, `null` clears; `children` are synced by name (extra children are never removed).
- `delete` — remove a node by path.
- `dump` — write `Library/AgentBridge/Artifacts/<id>/uidump.json`: the whole tree with anchors, sizes, `screenRect` in reference pixels, and object references of custom components.
- `shot` — render the prefab offscreen to `Library/AgentBridge/Artifacts/<id>/shot.png` (1920×1080 by default) plus a `.rects.json` with every node's screen rect; `outline` draws colored frames for the listed paths. Optional `output` is a PNG file name only, never a path. Absolute paths and directory segments are rejected, so every transient UI artifact remains owned by its task.

Order within a task: all `apply`/`delete` run first over the loaded prefab contents, then a single save, then `dump`/`shot` over the saved asset. If the prefab does not exist and there is an `apply`, it is created (root `RectTransform` stretched 0..1). Any error (bad JSON, missing prefab/sprite/type/path) yields `runtime_error` and leaves the prefab unchanged.

### Layout conventions (`UNITYAGENT-UI.md`)

Before laying out UI, the `unity-ui` skill recursively searches the project for a `UNITYAGENT-UI.md` file describing your layout conventions — reference resolution, palette, fonts, art paths, prefab paths, and custom view components. Create one so Claude uses your real colors, fonts and assets instead of guessing. Template with recommendations: `Docs/UNITYAGENT-UI-template.md`.

## Working Directory

```
Library/AgentBridge/
├── status.json                 ← protocol/package/project/Editor status and capabilities
├── heartbeat                   ← liveness marker, updated every ~2s
├── Inbox/
│   ├── Task_XXX.task.json      ← task request (Id, Kind, PayloadFile, ...)
│   ├── Task_XXX.cs             ← payload for a csharp task
│   └── Task_XXX.ui.json        ← payload for a ui task
├── Journal/
│   └── Task_XXX.json           ← single result record per task (TaskRecord)
├── Roslyn/                     ← downloaded Roslyn assemblies (NuGet source only)
└── Artifacts/
    └── <id>/                   ← removed together with the owning journal entry
        ├── uidump.json         ← UI dump output
        ├── shot.png            ← default UI screenshot output
        └── shot.png.rects.json ← screen rects for the screenshot
```

## Limitations

- Works only in Unity Editor, not in Play Mode (except `tests --mode PlayMode`, which enters Play Mode itself)
- Tasks are processed strictly one at a time, oldest first — Bridge does not start a new task while one is in flight
- `Run()` is invoked on Unity's main thread; awaited continuations resume there too, so heavy synchronous work still blocks the Editor — offload it via `await Task.Run(...)`. Bridge caps a task at `TaskTimeoutSeconds` (default 300, configurable in `ProjectSettings/AgentBridge.json`); on timeout it writes `Status: "timeout"` and unblocks the queue
- A running task can be aborted via **Tools → Agent Bridge → Cancel Running Task**
- `csharp` tasks compile against whatever assemblies are already loaded in the domain — they cannot reference project code that has compilation errors, since the broken assembly itself would never have loaded. Use a `compile` task first to confirm the project builds.
- Roslyn is not bundled with the package — Bridge resolves it from the project's own references, a local folder, or downloads it from NuGet on demand (**Tools → Agent Bridge → Setup...**). The Unity Editor's own bundled compiler is only usable as a source when it targets a runtime compatible with the Editor's own CLR.

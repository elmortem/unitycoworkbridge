# Unity Cowork Bridge

A system for executing AI-generated C# scripts in an open Unity Editor via Claude Cowork. Cowork writes the scripts, Bridge compiles and runs them inside Unity, then returns results and errors. On compilation errors, Cowork automatically fixes the code and retries.

## How It Works

The system consists of two parts:

**Cowork Bridge** — a C# package inside Unity Editor. It watches the `Assets/Editor/CoworkBridge/` folder, picks up task files, compiles scripts, executes them via reflection, and writes results. On load it also installs a small `wait-for-result.sh` helper into that folder (if missing), which Cowork uses to wait for results.

**Unity Bridge Plugin** — a plugin for Claude Cowork. It ships two skills:

- `unity-bridge` — instructions for Claude on script generation, the Bridge communication protocol, and error handling. It commands the Unity-side Bridge and auto-triggers on any Unity Editor task ("list all prefabs using shader X", "rename these assets"), or invoke it explicitly via `/unity-bridge`.
- `unity-ui` — declarative uGUI layout: creating/editing UI prefabs, dumping layout geometry, and screenshotting screens through `Task_*.ui.json` tasks (no C# compilation, no domain reload — iterations take seconds). Auto-triggers on layout phrasing ("build this popup", "move/recolor this element", "screenshot the screen"), or `/unity-ui`. uGUI + TMP only; UI Toolkit is not supported. See [Declarative UI Tasks](#declarative-ui-tasks).

There are two task types, both dropped into `Assets/Editor/CoworkBridge/` and processed sequentially in creation order:

- A **C# task** is the `.cs` script itself — Bridge compiles and runs its `Run()` method.
- A **UI task** is a `Task_*.ui.json` file — Bridge applies it to a prefab directly, without compilation. Multiple agents or users can create tasks independently.

## Installing Unity Bridge

### Option 1: Via Package Manager (Git URL)

1. Open **Window → Package Manager** in Unity Editor
2. Click **+** → **Add package from git URL...**
3. Enter: `https://github.com/elmortem/unitycoworkbridge.git?path=CoworkBridge`
4. Add `Assets/Editor/CoworkBridge/` to your project's `.gitignore`

### Option 2: Manual Copy

1. Copy the `CoworkBridge/` folder into the `Packages/` folder of your Unity project
2. Add `Assets/Editor/CoworkBridge/` to your project's `.gitignore`

The package has no dependencies on other project assemblies and will work even if the project has compilation errors.

## Installing Cowork Plugin

### Requirements

Cowork is only available in the Claude desktop application (macOS and Windows). The web version and mobile apps do not support Cowork and plugins.

### Option 1: Via Claude Code CLI

If you have Claude Code installed, you can load the plugin directly from a local folder:

```bash
claude --plugin-dir /path/to/unity-bridge-plugin
```

For permanent installation, create your own marketplace or use the `--plugin-dir` flag on each launch.

### Option 2: Via Cowork UI

1. Open Claude Desktop and go to the **Cowork** tab
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

In Unity Editor, open **Tools → Cowork Bridge → Start**. Bridge will start watching the `Assets/Editor/CoworkBridge/` folder.

### Stopping Bridge

**Tools → Cowork Bridge → Stop**

### Running Tasks via Cowork

Just describe the task in natural language — the `unity-bridge` skill auto-triggers on Unity Editor requests:

```
add a Rigidbody component to all objects with the Enemy tag
```

If you want to force the skill to handle a request, invoke it explicitly:

```
/unity-bridge add a Rigidbody component to all objects with the Enemy tag
```

Claude will generate a script, send it to Bridge, wait for the result, and show the outcome. If there are compilation errors, it will automatically fix the code and retry (up to 3 times).

### How Cowork Waits for Results

Bridge writes `result_<id>.json` and then an empty `result_<id>.done` marker. To wait for a task, the skill runs the helper that Bridge installs into the watched folder:

```bash
bash Assets/Editor/CoworkBridge/wait-for-result.sh <TaskName> <timeout-seconds>
```

The helper polls for the `.done` marker, then prints the result JSON to stdout (or a `{"status":"timeout"}` JSON and exit code 1 if it times out). Bridge auto-creates this script in the watched folder on every Editor load when it is missing, so you never place it by hand.

To stop Claude Code from asking for confirmation on every wait, allow this exact command in your settings — `~/.claude/settings.json` (all projects) or `.claude/settings.local.json` (per project):

```json
{
  "permissions": {
    "allow": [
      "Bash(bash Assets/Editor/CoworkBridge/wait-for-result.sh:*)"
    ]
  }
}
```

`wait-for-result.sh` is a bash script and must keep **LF** line endings — on Windows, CRLF breaks it. The package enforces this via `.gitattributes` (`*.sh text eol=lf`), so the file stays LF when Unity fetches the package. If you commit the watched folder in your own project instead of gitignoring it, add the same `.gitattributes` rule there too.

### Running Tasks Manually

You can create a script manually and run it via **Tools → Cowork Bridge → Run Task...** (a file dialog for selecting a .cs file).

The script must follow this template:

```csharp
using UnityEngine;
using UnityEditor;

public static class Task_20260226_143052
{
    public static string Run()
    {
        // your code
        return "result description";
    }
}
```

### Cleaning Up Tasks

Bridge cleans up **successful** tasks on its own:

- **Auto-trim** — while idle, Bridge keeps only the last N successful tasks (default 10), removing older ones together with all their outputs (`result_*`, `testresult_*`, and the task-owned `Artifacts/<id>/` directory). N is configurable via `KeepCompletedCount` in `ProjectSettings/CoworkBridge.json`.
- **Orphan sweep** — while idle and during any manual clean, Bridge also removes result files and `Artifacts/<id>/` directories whose owning task file (`.cs` / `.ui.json`) is already gone. This catches outputs written after their task was trimmed — notably `testresult_*`, produced asynchronously after a test run — which would otherwise pile up forever.
- **`clean.command`** — to remove **all** successful tasks at once, drop an empty file at `Assets/Editor/CoworkBridge/clean.command`. Bridge deletes every successful task and the command file itself. Don't create it while a test run is in progress.

Failed tasks (`compiler_error` / `runtime_error`) are left untouched by auto-cleanup. Manual cleanup is still available:

- **Tools → Cowork Bridge → Clean Completed** — removes completed tasks (script + result + marker)
- **Tools → Cowork Bridge → Clean All** — removes all tasks

## Custom Project APIs

If the project has custom APIs (libraries, tools, builders), you can describe them for Bridge so that Claude uses them when generating scripts. Create a `UNITYCOWORK.md` file next to the library code.

When executing a task, the skill recursively searches for all `UNITYCOWORK.md` files in the project and reads them. If the described API is suitable for the task, Claude will use it instead of the standard Unity Editor API.

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

Detailed template with recommendations: `Docs/UNITYCOWORK-template.md`

No separate documentation is needed for the standard Unity Editor API — Claude knows it out of the box.

## Declarative UI Tasks

Besides C# scripts, Bridge accepts a second task type for uGUI layout: a `Task_YYYYMMDD_HHMMSS.ui.json` file placed in `Assets/Editor/CoworkBridge/`. Bridge applies it to a prefab **directly**, without compiling C# or reloading the domain, so layout iterations take seconds. The task id is the file name without the `.ui.json` suffix; results are the usual `result_<id>.json` + `.done`. Scope is uGUI + TMP only — UI Toolkit is not supported.

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
- `dump` — write `Artifacts/<id>/uidump.json`: the whole tree with anchors, sizes, `screenRect` in reference pixels, and object references of custom components.
- `shot` — render the prefab offscreen to `Artifacts/<id>/shot.png` (1920×1080 by default) plus a `.rects.json` with every node's screen rect; `outline` draws colored frames for the listed paths. Optional `output` is a PNG file name only, never a path. Absolute paths and directory segments are rejected, so every transient UI artifact remains owned by its task.

Order within a task: all `apply`/`delete` run first over the loaded prefab contents, then a single save, then `dump`/`shot` over the saved asset. If the prefab does not exist and there is an `apply`, it is created (root `RectTransform` stretched 0..1). Any error (bad JSON, missing prefab/sprite/type/path) yields `runtime_error` and leaves the prefab unchanged.

### Layout conventions (`UNITYCOWORK-UI.md`)

Before laying out UI, the `unity-ui` skill recursively searches the project for a `UNITYCOWORK-UI.md` file describing your layout conventions — reference resolution, palette, fonts, art paths, prefab paths, and custom view components. Create one so Claude uses your real colors, fonts and assets instead of guessing. Template with recommendations: `Docs/UNITYCOWORK-UI-template.md`.

## Working Directory

```
Assets/Editor/CoworkBridge/
├── wait-for-result.sh          ← result-wait helper (auto-installed by Bridge)
├── Task_XXX.cs                 ← generated C# scripts = tasks
├── Task_XXX.ui.json            ← declarative UI tasks
├── result_<id>.json            ← execution results
├── result_<id>.done            ← result readiness markers
└── Artifacts/
    └── <id>/                   ← removed together with the owning task
        ├── uidump.json         ← UI dump output
        ├── shot.png            ← default UI screenshot output
        └── shot.png.rects.json ← screen rects for the screenshot
```

## Limitations

- Works only in Unity Editor, not in Play Mode
- Tasks are processed sequentially
- The `Run()` method executes on Unity's main thread — long operations may freeze the Editor
- Generated scripts must not depend on assemblies with compilation errors

# Unity Cowork Bridge

A system for executing AI-generated C# scripts in an open Unity Editor via Claude Cowork. Cowork writes the scripts, Bridge compiles and runs them inside Unity, then returns results and errors. On compilation errors, Cowork automatically fixes the code and retries.

## How It Works

The system consists of two parts:

**Cowork Bridge** — a C# package inside Unity Editor. It watches the `Assets/Editor/CoworkBridge/` folder, picks up task files, compiles scripts, executes them via reflection, and writes results.

**Unity Bridge Plugin** — a plugin for Claude Cowork. It contains instructions for Claude on script generation, the Bridge communication protocol, and error handling logic.

A task is the `.cs` script itself — just place it in `Assets/Editor/CoworkBridge/`, and Bridge will pick it up. No additional JSON task files are needed. Multiple agents or users can create scripts independently — Bridge processes them sequentially in creation order.

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
├── commands/
│   └── unity.md             ← /unity command
├── skills/
│   └── unity-bridge/
│       └── SKILL.md         ← instructions for Claude
└── scripts/
    └── wait_result.sh       ← result waiting script
```

### Verifying Installation

After installation, the `/unity` command should be available in Cowork. Type it in the chat — if the plugin is installed correctly, Claude will start generating a script.

## Usage

### Starting Bridge

In Unity Editor, open **Tools → Cowork Bridge → Start**. Bridge will start watching the `Assets/Editor/CoworkBridge/` folder.

### Stopping Bridge

**Tools → Cowork Bridge → Stop**

### Running Tasks via Cowork

Use the `/unity` command with a natural language task description:

```
/unity add a Rigidbody component to all objects with the Enemy tag
```

Claude will generate a script, send it to Bridge, wait for the result, and show the outcome. If there are compilation errors, it will automatically fix the code and retry (up to 3 times).

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

## Working Directory

```
Assets/Editor/CoworkBridge/
├── Task_XXX.cs                 ← generated scripts = tasks
├── result_<id>.json            ← execution results
└── result_<id>.done            ← result readiness markers
```

## Limitations

- Works only in Unity Editor, not in Play Mode
- Tasks are processed sequentially
- The `Run()` method executes on Unity's main thread — long operations may freeze the Editor
- Generated scripts must not depend on assemblies with compilation errors

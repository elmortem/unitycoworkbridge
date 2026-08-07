Status: Выполнено

# TDD: Команда `shot` — скриншоты Scene View для агента

## Цель

Новый вид таска `shot` в Agent Bridge: агент декларативным JSON заказывает один или несколько скриншотов текущей открытой сцены с заданного ракурса. Механика захвата портируется из тулы SceneViewShot (`pcg4u-addons/PCGAddons/Assets/Plugins/SceneViewShot`): временное окно `SceneView` нужного размера + `InternalEditorUtility.ReadScreenPixel`.

## Обзор

- CLI: новая команда `agentbridge shot <file.shot.json>`, работает как `ui` — payload копируется в Inbox, создаётся `.task.json` c `Kind = "shot"`.
- Editor: `TaskCoordinator` диспатчит kind `shot` в новый `ShotTaskExecutor`, который асинхронно (через цепочку `EditorApplication.delayCall`, как в исходной туле) снимает каждый шот и завершается.
- PNG кладутся в `TaskContext.ArtifactsDirectory`, пути попадают в `Artifacts` журнала — как у ui-скриншотов.
- Сцена — только текущая открытая. Preflight сцен (`RequiresScenePreflight`) для `shot` НЕ выполняется: таск ничего не изменяет, снимает сцену как есть, включая несохранённое состояние.
- Ошибка на любом шоте — весь таск завершается `runtime_error` (fail fast); уже снятые PNG остаются в `Artifacts`.

## Формат payload `<TaskId>.shot.json`

```json
{
	"shots": [
		{
			"name": "hero_closeup",
			"width": 1280,
			"height": 720,
			"gizmos": true,
			"frame": { "target": "Level/Hero", "margin": 1.1, "rotation": [30, 45, 0], "orthographic": false }
		},
		{
			"name": "overview",
			"pose": { "pivot": [0, 0, 0], "rotation": [90, 0, 0], "size": 40, "orthographic": true }
		}
	]
}
```

Правила:

- `shots` — обязательный непустой массив.
- `name` — обязательная непустая строка; имя PNG-файла (недопустимые символы заменяются на `_`, дубликаты внутри таска получают суффикс `_2`, `_3`, ...).
- `width`/`height` — опционально, дефолт 1280x720, диапазон 16..8192. Фактический размер ограничен монитором (кламп с warning в `Logs`).
- `gizmos` — опционально, дефолт `true`.
- Ровно одно из `pose` / `frame`:
  - `pose` — явная поза SceneView: `pivot` `[x,y,z]` (обязателен), `rotation` `[x,y,z]` эйлеры в градусах (обязателен), `size` > 0 (обязателен), `orthographic` дефолт `false`.
  - `frame` — автокадрирование объекта: `target` (обязателен) — путь `Root/Child/Sub` от корня сцены или просто имя объекта (поиск в глубину по всем загруженным сценам, включая неактивные объекты); `margin` дефолт 1.1; `rotation` дефолт `[30, 45, 0]`; `orthographic` дефолт `false`.

## Результат таска

- `Status`: `success` / `runtime_error` / `rejected` / `timeout` / `canceled` — стандартные.
- `ReturnValue`: `"shot hero_closeup -> <abs path> (1280x720); shot overview -> ..."`.
- `Artifacts`: абсолютные пути PNG.
- `Logs`: варнинги клампа разрешения и сообщения об ошибках.

## Unity-пакет: новые файлы

Все файлы — в `Packages/com.elmortem.agentbridge/Editor/Shot/`, asmdef не меняется (папка входит в существующий `AgentBridge.asmdef`).

### SceneShotPose.cs

```csharp
using UnityEngine;

namespace AgentBridge.Shot
{
	public struct SceneShotPose
	{
		public Vector3 Pivot;
		public Quaternion Rotation;
		public float Size;
		public bool Orthographic;
	}
}
```

### SceneShotPoseMode.cs

```csharp
namespace AgentBridge.Shot
{
	public enum SceneShotPoseMode
	{
		Explicit,
		Frame
	}
}
```

### SceneShotItem.cs

```csharp
using UnityEngine;

namespace AgentBridge.Shot
{
	public class SceneShotItem
	{
		public string Name;
		public int Width;
		public int Height;
		public bool Gizmos;
		public SceneShotPoseMode Mode;
		public SceneShotPose Pose;
		public string FrameTarget;
		public float FrameMargin;
		public Vector3 FrameRotation;
		public bool Orthographic;
	}
}
```

### ShotPayloadParser.cs

Парсинг через существующие `AgentBridge.Ui.UiJson` и `AgentBridge.Ui.UiValue`.

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using AgentBridge.Ui;

namespace AgentBridge.Shot
{
	public static class ShotPayloadParser
	{
		public static List<SceneShotItem> Parse(string json)
		{
			object parsed = UiJson.Parse(json);
			if (!(parsed is Dictionary<string, object> doc))
			{
				throw new Exception("payload root is not a JSON object");
			}

			if (!(doc.TryGetValue("shots", out object shotsObj) && shotsObj is IList<object> shots) || shots.Count == 0)
			{
				throw new Exception("payload is missing a non-empty 'shots' array");
			}

			List<SceneShotItem> items = new List<SceneShotItem>();
			foreach (object shotObj in shots)
			{
				if (!(shotObj is Dictionary<string, object> shot))
				{
					throw new Exception("'shots' entry is not a JSON object");
				}

				items.Add(ParseItem(shot));
			}

			return items;
		}

		private static SceneShotItem ParseItem(Dictionary<string, object> shot)
		{
			SceneShotItem item = new SceneShotItem();

			if (!(shot.TryGetValue("name", out object nameObj) && nameObj is string name) || string.IsNullOrEmpty(name))
			{
				throw new Exception("shot is missing 'name'");
			}

			item.Name = name;
			item.Width = shot.TryGetValue("width", out object w) ? UiValue.I(w) : 1280;
			item.Height = shot.TryGetValue("height", out object h) ? UiValue.I(h) : 720;
			if (item.Width < 16 || item.Width > 8192 || item.Height < 16 || item.Height > 8192)
			{
				throw new Exception("shot '" + name + "': width/height must be within 16..8192");
			}

			item.Gizmos = !shot.TryGetValue("gizmos", out object g) || UiValue.B(g);

			bool hasPose = shot.TryGetValue("pose", out object poseObj);
			bool hasFrame = shot.TryGetValue("frame", out object frameObj);
			if (hasPose == hasFrame)
			{
				throw new Exception("shot '" + name + "': exactly one of 'pose' or 'frame' is required");
			}

			if (hasPose)
			{
				item.Mode = SceneShotPoseMode.Explicit;
				item.Pose = ParsePose(name, poseObj);
				item.Orthographic = item.Pose.Orthographic;
			}
			else
			{
				item.Mode = SceneShotPoseMode.Frame;
				ParseFrame(name, frameObj, item);
			}

			return item;
		}

		private static SceneShotPose ParsePose(string name, object poseObj)
		{
			if (!(poseObj is Dictionary<string, object> pose))
			{
				throw new Exception("shot '" + name + "': 'pose' is not a JSON object");
			}

			if (!pose.TryGetValue("pivot", out object pivot))
			{
				throw new Exception("shot '" + name + "': 'pose' is missing 'pivot'");
			}

			if (!pose.TryGetValue("rotation", out object rotation))
			{
				throw new Exception("shot '" + name + "': 'pose' is missing 'rotation'");
			}

			if (!pose.TryGetValue("size", out object size))
			{
				throw new Exception("shot '" + name + "': 'pose' is missing 'size'");
			}

			SceneShotPose result = new SceneShotPose();
			result.Pivot = UiValue.V3(pivot);
			result.Rotation = Quaternion.Euler(UiValue.V3(rotation));
			result.Size = UiValue.F(size);
			result.Orthographic = pose.TryGetValue("orthographic", out object ortho) && UiValue.B(ortho);
			if (result.Size <= 0f)
			{
				throw new Exception("shot '" + name + "': 'pose.size' must be positive");
			}

			return result;
		}

		private static void ParseFrame(string name, object frameObj, SceneShotItem item)
		{
			if (!(frameObj is Dictionary<string, object> frame))
			{
				throw new Exception("shot '" + name + "': 'frame' is not a JSON object");
			}

			if (!(frame.TryGetValue("target", out object targetObj) && targetObj is string target) || string.IsNullOrEmpty(target))
			{
				throw new Exception("shot '" + name + "': 'frame' is missing 'target'");
			}

			item.FrameTarget = target;
			item.FrameMargin = frame.TryGetValue("margin", out object margin) ? UiValue.F(margin) : 1.1f;
			if (item.FrameMargin <= 0f)
			{
				throw new Exception("shot '" + name + "': 'frame.margin' must be positive");
			}

			item.FrameRotation = frame.TryGetValue("rotation", out object rotation) ? UiValue.V3(rotation) : new Vector3(30f, 45f, 0f);
			item.Orthographic = frame.TryGetValue("orthographic", out object ortho) && UiValue.B(ortho);
		}
	}
}
```

### SceneShotFramer.cs

```csharp
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge.Shot
{
	public static class SceneShotFramer
	{
		public static SceneShotPose Frame(string target, float margin, Vector3 rotationEuler, bool orthographic)
		{
			GameObject go = Resolve(target);
			if (go == null)
			{
				throw new Exception("frame target not found in loaded scenes: " + target);
			}

			Bounds bounds = ComputeBounds(go);
			SceneShotPose pose = new SceneShotPose();
			pose.Pivot = bounds.center;
			pose.Rotation = Quaternion.Euler(rotationEuler);
			pose.Size = Mathf.Max(bounds.extents.magnitude, 0.01f) * margin;
			pose.Orthographic = orthographic;
			return pose;
		}

		private static GameObject Resolve(string target)
		{
			int separator = target.IndexOf('/');
			string rootName = separator < 0 ? target : target.Substring(0, separator);
			string rest = separator < 0 ? null : target.Substring(separator + 1);

			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				Scene scene = SceneManager.GetSceneAt(i);
				if (!scene.isLoaded)
				{
					continue;
				}

				foreach (GameObject root in scene.GetRootGameObjects())
				{
					if (rest == null)
					{
						GameObject found = FindByName(root.transform, target);
						if (found != null)
						{
							return found;
						}
					}
					else if (root.name == rootName)
					{
						Transform child = root.transform.Find(rest);
						if (child != null)
						{
							return child.gameObject;
						}
					}
				}
			}

			return null;
		}

		private static GameObject FindByName(Transform node, string name)
		{
			if (node.name == name)
			{
				return node.gameObject;
			}

			for (int i = 0; i < node.childCount; i++)
			{
				GameObject found = FindByName(node.GetChild(i), name);
				if (found != null)
				{
					return found;
				}
			}

			return null;
		}

		private static Bounds ComputeBounds(GameObject go)
		{
			Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
			if (renderers.Length == 0)
			{
				return new Bounds(go.transform.position, Vector3.one);
			}

			Bounds bounds = renderers[0].bounds;
			for (int i = 1; i < renderers.Length; i++)
			{
				bounds.Encapsulate(renderers[i].bounds);
			}

			return bounds;
		}
	}
}
```

### ShotTaskExecutor.cs

Порт `SceneViewCaptureService` из SceneViewShot, обёрнутый в исполнителя, которого поллит координатор. Офсеты окна — константы с дефолтами исходной тулы. Окно помечается заголовком `AgentBridge Shot`, чтобы после domain reload осиротевшие окна можно было закрыть.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Shot
{
	public class ShotTaskExecutor
	{
		public const string WindowTitle = "AgentBridge Shot";

		private const int OffsetTop = 46;
		private const int OffsetBottom = 0;
		private const int OffsetLeft = 2;
		private const int OffsetRight = 2;

		private readonly TaskContext _context;
		private readonly List<SceneShotItem> _items;
		private readonly List<string> _logs = new List<string>();
		private readonly List<string> _summary = new List<string>();
		private readonly HashSet<string> _usedFileNames = new HashSet<string>();

		private int _index;
		private SceneView _window;
		private bool _completed;
		private string _status = "success";

		private ShotTaskExecutor(TaskContext context, List<SceneShotItem> items)
		{
			_context = context;
			_items = items;
		}

		public bool IsCompleted
		{
			get { return _completed; }
		}

		public static ShotTaskExecutor Begin(string payloadPath, TaskContext context)
		{
			List<SceneShotItem> items = ShotPayloadParser.Parse(File.ReadAllText(payloadPath));
			ShotTaskExecutor executor = new ShotTaskExecutor(context, items);
			executor.StartNext();
			return executor;
		}

		public static void CloseOrphanWindows()
		{
			foreach (SceneView view in Resources.FindObjectsOfTypeAll<SceneView>())
			{
				if (view.titleContent != null && view.titleContent.text == WindowTitle)
				{
					view.Close();
				}
			}
		}

		public TaskResultData GetResult()
		{
			return new TaskResultData
			{
				Status = _status,
				ReturnValue = string.Join("; ", _summary),
				Logs = _logs
			};
		}

		private void StartNext()
		{
			if (_index >= _items.Count)
			{
				_completed = true;
				return;
			}

			SceneShotItem item = _items[_index];

			try
			{
				SceneShotPose pose = item.Mode == SceneShotPoseMode.Frame
					? SceneShotFramer.Frame(item.FrameTarget, item.FrameMargin, item.FrameRotation, item.Orthographic)
					: item.Pose;
				Capture(item, pose);
			}
			catch (Exception ex)
			{
				Fail("shot '" + item.Name + "': " + ex.Message);
			}
		}

		private void Capture(SceneShotItem item, SceneShotPose pose)
		{
			int ppp = Mathf.Max(1, Mathf.RoundToInt(EditorGUIUtility.pixelsPerPoint));
			Rect workArea = EditorGUIUtility.GetMainWindowPosition();
			Vector2Int targetPx = ClampToWorkArea(item, ppp, workArea);

			int windowWidthPx = targetPx.x + OffsetLeft + OffsetRight;
			int windowHeightPx = targetPx.y + OffsetTop + OffsetBottom;

			_window = EditorWindow.CreateWindow<SceneView>();
			_window.titleContent = new GUIContent(WindowTitle);
			_window.drawGizmos = item.Gizmos;
			_window.LookAt(pose.Pivot, pose.Rotation, pose.Size, pose.Orthographic, true);
			_window.position = new Rect(workArea.x, workArea.y, windowWidthPx / (float)ppp, windowHeightPx / (float)ppp);
			_window.Focus();
			_window.Repaint();

			EditorApplication.delayCall += () =>
			{
				if (IsCanceled())
				{
					return;
				}

				_window.Focus();
				_window.Repaint();

				EditorApplication.delayCall += () =>
				{
					if (IsCanceled())
					{
						return;
					}

					try
					{
						Write(item, targetPx, ppp);
						CloseWindow();
						_index++;
						StartNext();
					}
					catch (Exception ex)
					{
						Fail("shot '" + item.Name + "': " + ex.Message);
					}
				};
			};
		}

		private Vector2Int ClampToWorkArea(SceneShotItem item, int ppp, Rect workArea)
		{
			int maxWidthPx = Mathf.FloorToInt(workArea.width * ppp) - OffsetLeft - OffsetRight;
			int maxHeightPx = Mathf.FloorToInt(workArea.height * ppp) - OffsetTop - OffsetBottom;

			float scale = 1f;
			if (item.Width > maxWidthPx)
			{
				scale = Mathf.Min(scale, maxWidthPx / (float)item.Width);
			}

			if (item.Height > maxHeightPx)
			{
				scale = Mathf.Min(scale, maxHeightPx / (float)item.Height);
			}

			if (scale < 1f)
			{
				int clampedWidth = Mathf.FloorToInt(item.Width * scale);
				int clampedHeight = Mathf.FloorToInt(item.Height * scale);
				_logs.Add("shot '" + item.Name + "': requested " + item.Width + "x" + item.Height
					+ " does not fit the screen, clamped to " + clampedWidth + "x" + clampedHeight);
				return new Vector2Int(clampedWidth, clampedHeight);
			}

			return new Vector2Int(item.Width, item.Height);
		}

		private void Write(SceneShotItem item, Vector2Int targetPx, int ppp)
		{
			int originX = Mathf.RoundToInt(_window.position.x * ppp) + OffsetLeft;
			int originY = Mathf.RoundToInt(_window.position.y * ppp) + OffsetTop;

			Color[] pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(
				new Vector2(originX, originY), targetPx.x, targetPx.y);

			Texture2D tex = new Texture2D(targetPx.x, targetPx.y, TextureFormat.RGB24, false);
			tex.SetPixels(pixels);
			byte[] png = tex.EncodeToPNG();
			UnityEngine.Object.DestroyImmediate(tex);

			Directory.CreateDirectory(_context.ArtifactsDirectory);
			string fileName = BuildFileName(item.Name);
			string fullPath = Path.Combine(_context.ArtifactsDirectory, fileName);
			File.WriteAllBytes(fullPath, png);

			_context.AddArtifact(fullPath);
			_summary.Add("shot " + item.Name + " -> " + fullPath + " (" + targetPx.x + "x" + targetPx.y + ")");
		}

		private string BuildFileName(string name)
		{
			string safeName = name;
			foreach (char invalid in Path.GetInvalidFileNameChars())
			{
				safeName = safeName.Replace(invalid, '_');
			}

			string fileName = safeName + ".png";
			int suffix = 2;
			while (_usedFileNames.Contains(fileName))
			{
				fileName = safeName + "_" + suffix + ".png";
				suffix++;
			}

			_usedFileNames.Add(fileName);
			return fileName;
		}

		private bool IsCanceled()
		{
			if (!_context.CancellationToken.IsCancellationRequested)
			{
				return false;
			}

			CloseWindow();
			_completed = true;
			return true;
		}

		private void Fail(string message)
		{
			_logs.Add(message);
			_status = "runtime_error";
			CloseWindow();
			_completed = true;
		}

		private void CloseWindow()
		{
			if (_window != null)
			{
				_window.Close();
				_window = null;
			}
		}
	}
}
```

## Unity-пакет: изменения в TaskCoordinator.cs

- Добавить поля рядом с `_activeCSharpExecutor`:

```csharp
private static Shot.ShotTaskExecutor _activeShotExecutor;
private static TaskContext _activeShotContext;
```

- В `Start()` после `FinalizeOrphanRecords()` добавить вызов:

```csharp
Shot.ShotTaskExecutor.CloseOrphanWindows();
```

- В `OnUpdate()` в цепочку поллинга добавить ветку после `PollCSharpExecutor()`:

```csharp
else if (_activeShotExecutor != null)
{
	PollShotExecutor();
}
```

- В `RunTask` в `switch (request.Kind)` добавить кейс перед `default`:

```csharp
case "shot":
	StartShotTask(request);
	break;
```

- Добавить методы:

```csharp
private static void StartShotTask(TaskRequest request)
{
	string payloadPath = Path.Combine(BridgePaths.Inbox, request.Id + ".shot.json");
	if (!File.Exists(payloadPath))
	{
		FinishTask("rejected", null, new List<string> { "payload file not found: " + request.Id + ".shot.json" }, false);
		return;
	}

	_activeShotContext = new TaskContext
	{
		Id = request.Id,
		Kind = request.Kind,
		CancellationToken = _activeCancellation.Token
	};
	_activeShotExecutor = Shot.ShotTaskExecutor.Begin(payloadPath, _activeShotContext);
}

private static void PollShotExecutor()
{
	if (_activeRecord == null || !_activeShotExecutor.IsCompleted)
	{
		return;
	}

	TaskResultData result = _activeShotExecutor.GetResult();
	foreach (string artifact in _activeShotContext.Artifacts)
	{
		_activeRecord.Artifacts.Add(artifact);
	}

	_activeShotExecutor = null;
	_activeShotContext = null;
	FinishTask(result.Status, result.ReturnValue, result.Logs, false);
}
```

- В `CleanupActive()` добавить:

```csharp
_activeShotExecutor = null;
_activeShotContext = null;
```

- `RequiresScenePreflight` не менять — `shot` в список не входит.

Поведение при таймауте/отмене: `CheckTimeout`/`CancelActive` отменяют токен и завершают запись; висящий `delayCall` исполнителя увидит отменённый токен через `IsCanceled()` и закроет окно.

## CLI: изменения

### AgentBridgeApplication.cs

- В `switch (command)` добавить кейс после `"ui"`:

```csharp
case "shot":
	if (commandArguments.Length != 1)
	{
		return WriteError("bad_usage", "usage: agentbridge shot <file.shot.json> [--project <path>] [--wait <seconds>] [--format json|human]", options.Format);
	}

	WarnIfPayloadInsideAssets(paths, commandArguments[0]);
	return await client.SubmitPayloadAsync("shot", commandArguments[0], options.WaitSeconds);
```

- В `WriteHelp()` в список commands после строки `ui <file.ui.json>` добавить строку:

```
  shot <file.shot.json>
```

### BridgeClient.cs

- В `SubmitPayloadAsync` заменить вычисление `payloadName`:

```csharp
var payloadName = kind switch
{
	"ui" => taskId + ".ui.json",
	"shot" => taskId + ".shot.json",
	_ => taskId + ".cs"
};
```

- В `GetPayloadTaskId` добавить ветку перед возвратом по умолчанию:

```csharp
if (kind == "shot" && fileName.EndsWith(".shot.json", StringComparison.OrdinalIgnoreCase))
{
	return fileName[..^10];
}
```

`TaskResultFormatter` не меняется: `Artifacts` уже выводится для ui-тасков тем же путём.

## Версии

- `Packages/com.elmortem.agentbridge/package.json`: `"version": "0.10.0"` → `"0.11.0"`.
- `AgentBridgeCli/AgentBridgeCli.csproj`: `<Version>1.7.0</Version>` → `<Version>1.8.0</Version>`.
- `BridgeConstants.ProtocolVersion` не меняется: новый kind обратно совместим, старый редактор ответит `rejected: unknown kind`.

## Скилл unity-bridge: unity-bridge-plugin/skills/unity-bridge/SKILL.md

- В frontmatter `description` перед фразой `For uGUI prefab layout...` добавить: `Also use for capturing Scene View screenshots of the open scene ('сфоткай сцену', 'скриншот сцены', 'покажи как выглядит уровень') via the declarative shot command.`
- В раздел «Команды CLI» после `### csharp <path-to-cs>` добавить подраздел:

```markdown
### `shot <file.shot.json>`

Скриншоты текущей открытой сцены (Scene View) с заданных ракурсов. Декларативный JSON, без компиляции. Файл пиши в `Temp/AgentBridge/<TaskName>.shot.json`.

Формат:

    {
      "shots": [
        { "name": "hero", "width": 1280, "height": 720,
          "frame": { "target": "Level/Hero", "margin": 1.1, "rotation": [30, 45, 0] } },
        { "name": "top",
          "pose": { "pivot": [0, 0, 0], "rotation": [90, 0, 0], "size": 40, "orthographic": true } }
      ]
    }

- Ровно одно из `pose` (явная поза SceneView: pivot/rotation/size/orthographic) или `frame` (автокадрирование объекта по имени или пути `Root/Child`, как клавиша F).
- `width`/`height` — дефолт 1280x720. Снимок делается с реального экрана: редактор должен быть виден и не свёрнут, разрешение больше монитора клампится (warning в `Logs`).
- `gizmos` — дефолт `true`.
- Пути готовых PNG приходят в поле `Artifacts` результата — читай их обычным просмотром изображений.
- Снимается текущая открытая сцена. Нужна другая — сначала открой её отдельным `csharp`-таском.
```

## Проверка

- Пересобрать CLI (`dotnet build AgentBridgeCli -c Release`), убедиться что версия `agentbridge --version` = 1.8.0.
- В открытом Unity-проекте выполнить `agentbridge compile` — пакет собирается без ошибок.
- Смоук: создать `Temp/AgentBridge/Task_shot_smoke.shot.json` с одним `frame`-шотом на любой объект сцены и одним `pose`-шотом, выполнить `agentbridge shot`, убедиться: `Status == "success"`, PNG существуют по путям из `Artifacts`, содержимое соответствует ракурсам.
- Кламп: запросить 8000x8000, убедиться что в `Logs` есть warning и PNG уменьшен.
- Ошибка: указать несуществующий `frame.target`, убедиться что `Status == "runtime_error"` и временное окно SceneView не осталось открытым.

## Отклонения при реализации

- Двойного `EditorApplication.delayCall` не хватает: свежесозданное окно успевает отрисоваться лишь частично, и PNG получался пустым (проверено на 935x935). Вместо цепочки `delayCall` исполнитель подписывается на `EditorApplication.update` и «отстаивает» окно: 0.4 c повторяет `Focus`/`Repaint` каждый тик, затем просит последнюю отрисовку и через 0.2 c читает экран.
- Перед чтением экрана размер снимка дополнительно ограничивается фактическим `_window.position` (`ClampToWindow`) — если оконный менеджер выдал окно меньше запрошенного, в PNG не попадут пиксели рабочего стола; факт усечения пишется в `Logs`.
- В `BridgeStatusWriter.Current.Capabilities` добавлен `"shot"`.

## После выполнения

- Измени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию проекта (README, шаблоны UNITYAGENT) под новую команду.

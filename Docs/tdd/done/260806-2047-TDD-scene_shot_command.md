Status: Выполнено

# TDD: Команда `shot` — скриншоты Scene View для агента

## Цель

Новый вид таска `shot` в Agent Bridge: агент декларативным JSON заказывает один или несколько скриншотов текущей открытой сцены с заданного ракурса и получает PNG в артефактах таска.

Захват выполняется через принудительную перерисовку вью в `RenderTexture` (`UnityEditor.GUIView.GrabPixels`), а не чтением пикселей экрана. Это даёт полноценную картинку Scene View вместе с гизмо, иконками компонентов и сеткой, и при этом не зависит от того, что происходит на экране: перекрытие окна Unity другим приложением, потеря фокуса и полная минимизация редактора на результат не влияют (проверено — снимки побайтово идентичны).

## Обзор

- CLI: новая команда `agentbridge shot <file.shot.json>`, работает как `ui` — payload копируется в Inbox, создаётся `.task.json` c `Kind = "shot"`.
- Editor: `TaskCoordinator` диспатчит kind `shot` в `ShotTaskExecutor` — конечный автомат, который координатор поллит из `OnUpdate`, по одному шоту за проход.
- Каждый шот: создаётся временное окно `SceneView`, гасятся оверлеи, применяется поза, окно отстаивается 0.5 с редакторского времени, содержимое вью снимается в `RenderTexture`, пишется PNG, окно закрывается.
- PNG кладутся в `TaskContext.ArtifactsDirectory`, пути попадают в `Artifacts` журнала — как у ui-скриншотов.
- Сцена — только текущая открытая. Preflight сцен (`RequiresScenePreflight`) для `shot` НЕ выполняется: таск ничего не изменяет и снимает сцену как есть, включая несохранённое состояние.
- Ошибка на любом шоте — весь таск завершается `runtime_error` (fail fast); уже снятые PNG остаются в `Artifacts`.

## Ограничения механики, влияющие на реализацию

- Окно Scene View — настоящее окно ОС, поэтому его размер ограничен рабочей областью экрана. `RenderTexture` больше окна лишних пикселей не даёт: контент рисуется в углу в родном размере, остальное — мусор. Отсюда политика размера ниже.
- `GrabPixels` возвращает изображение перевёрнутым по вертикали — строки разворачиваются перед кодированием в PNG.
- Цветовое пространство `RenderTexture` обязано соответствовать проекту: при `ColorSpace.Linear` нужен `RenderTextureReadWrite.Linear`, иначе картинка выходит белёсой.
- Смещения окна из старых экранных реализаций (заголовок, рамки) не нужны: снимается вью целиком.

## Политика размера

- Дефолт запроса: 1280×720.
- Жёсткий потолок: 1920×1080. Запрос больше потолка обрезается до потолка.
- Доступный размер в пикселях: `workArea * ppp` минус бордер 24 px с каждой стороны (48 px по каждой оси).
- Если запрошенный (после потолка) размер не влезает в доступный — он уменьшается **пропорционально**, единым коэффициентом по обеим осям: соотношение сторон задаёт кадр камеры, покомпонентный кламп изменил бы ракурс, заказанный агентом.
- Факт уменьшения — предупреждением в `Logs`; фактическое разрешение — в `ReturnValue`.

## Формат payload `<TaskId>.shot.json`

```json
{
	"shots": [
		{
			"name": "hero_closeup",
			"width": 1280,
			"height": 720,
			"gizmos": true,
			"grid": false,
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
- `width`/`height` — опционально, дефолт 1280×720, допустимый диапазон запроса 16..1920 и 16..1080 соответственно; фактический размер определяется политикой выше.
- `gizmos` — опционально, дефолт `true`.
- `grid` — опционально, дефолт `false`.
- Ровно одно из `pose` / `frame`:
  - `pose` — явная поза SceneView: `pivot` `[x,y,z]` (обязателен), `rotation` `[x,y,z]` эйлеры в градусах (обязателен), `size` > 0 (обязателен), `orthographic` дефолт `false`.
  - `frame` — автокадрирование объекта: `target` (обязателен) — путь `Root/Child/Sub` от корня сцены или просто имя объекта (поиск в глубину по всем загруженным сценам, включая неактивные объекты); `margin` дефолт 1.1; `rotation` дефолт `[30, 45, 0]`; `orthographic` дефолт `false`.

## Результат таска

- `Status`: `success` / `runtime_error` / `rejected` / `timeout` / `canceled` — стандартные.
- `ReturnValue`: `"shot hero_closeup -> <abs path> (1280x720); shot overview -> ..."`.
- `Artifacts`: абсолютные пути PNG.
- `Logs`: предупреждения об уменьшении размера, о недоступности API оверлеев и сообщения об ошибках.

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
		public bool Grid;
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
		public const int MaxWidth = 1920;
		public const int MaxHeight = 1080;

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
			if (item.Width < 16 || item.Width > MaxWidth || item.Height < 16 || item.Height > MaxHeight)
			{
				throw new Exception("shot '" + name + "': width must be 16.." + MaxWidth + " and height 16.." + MaxHeight);
			}

			item.Gizmos = !shot.TryGetValue("gizmos", out object g) || UiValue.B(g);
			item.Grid = shot.TryGetValue("grid", out object grid) && UiValue.B(grid);

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

### SceneViewGrabber.cs

Обёртка над внутренним API редактора. `MethodInfo` и `FieldInfo` резолвятся один раз и кэшируются.

```csharp
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Shot
{
	public static class SceneViewGrabber
	{
		private static FieldInfo _parentField;
		private static MethodInfo _grabPixels;
		private static PropertyInfo _overlayCanvas;
		private static MethodInfo _setOverlaysEnabled;
		private static bool _resolved;

		public static void HideOverlays(EditorWindow window, Action<string> warn)
		{
			Resolve();

			if (_overlayCanvas == null)
			{
				warn("scene shot: EditorWindow.overlayCanvas is not available, editor overlays will appear in the image");
				return;
			}

			object canvas = _overlayCanvas.GetValue(window);
			if (canvas == null)
			{
				warn("scene shot: overlayCanvas is null, editor overlays will appear in the image");
				return;
			}

			if (_setOverlaysEnabled == null)
			{
				_setOverlaysEnabled = canvas.GetType().GetMethod(
					"SetOverlaysEnabled",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
					null,
					new[] { typeof(bool) },
					null);
			}

			if (_setOverlaysEnabled == null)
			{
				warn("scene shot: OverlayCanvas.SetOverlaysEnabled is not available, editor overlays will appear in the image");
				return;
			}

			_setOverlaysEnabled.Invoke(canvas, new object[] { false });
		}

		public static Texture2D Grab(EditorWindow window, int width, int height)
		{
			Resolve();

			if (_parentField == null || _grabPixels == null)
			{
				throw new Exception("this Unity version does not expose GUIView.GrabPixels(RenderTexture, Rect)");
			}

			object host = _parentField.GetValue(window);
			if (host == null)
			{
				throw new Exception("EditorWindow.m_Parent is null");
			}

			RenderTextureReadWrite readWrite = QualitySettings.activeColorSpace == ColorSpace.Linear
				? RenderTextureReadWrite.Linear
				: RenderTextureReadWrite.Default;

			RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, readWrite);
			rt.Create();

			Texture2D texture = null;

			try
			{
				_grabPixels.Invoke(host, new object[] { rt, new Rect(0f, 0f, width, height) });

				RenderTexture previous = RenderTexture.active;
				RenderTexture.active = rt;
				texture = new Texture2D(width, height, TextureFormat.RGB24, false);
				texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
				texture.Apply();
				RenderTexture.active = previous;

				FlipVertically(texture, width, height);
				return texture;
			}
			catch
			{
				if (texture != null)
				{
					UnityEngine.Object.DestroyImmediate(texture);
				}

				throw;
			}
			finally
			{
				rt.Release();
				UnityEngine.Object.DestroyImmediate(rt);
			}
		}

		private static void FlipVertically(Texture2D texture, int width, int height)
		{
			Color[] pixels = texture.GetPixels();
			Color[] flipped = new Color[pixels.Length];
			for (int row = 0; row < height; row++)
			{
				Array.Copy(pixels, row * width, flipped, (height - 1 - row) * width, width);
			}

			texture.SetPixels(flipped);
			texture.Apply();
		}

		private static void Resolve()
		{
			if (_resolved)
			{
				return;
			}

			_resolved = true;

			_parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
			_overlayCanvas = typeof(EditorWindow).GetProperty(
				"overlayCanvas",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

			if (_parentField == null)
			{
				return;
			}

			Type hostType = _parentField.FieldType;
			while (hostType != null && _grabPixels == null)
			{
				_grabPixels = hostType.GetMethod(
					"GrabPixels",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
					null,
					new[] { typeof(RenderTexture), typeof(Rect) },
					null);

				hostType = hostType.BaseType;
			}
		}
	}
}
```

### SceneShotResolution.cs

```csharp
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Shot
{
	public static class SceneShotResolution
	{
		public const int Border = 24;

		public static Vector2Int Fit(int requestedWidth, int requestedHeight, int ppp, Rect workArea)
		{
			int availableWidth = Mathf.FloorToInt(workArea.width * ppp) - Border * 2;
			int availableHeight = Mathf.FloorToInt(workArea.height * ppp) - Border * 2;

			float scale = 1f;
			if (requestedWidth > availableWidth)
			{
				scale = Mathf.Min(scale, availableWidth / (float)requestedWidth);
			}

			if (requestedHeight > availableHeight)
			{
				scale = Mathf.Min(scale, availableHeight / (float)requestedHeight);
			}

			if (scale >= 1f)
			{
				return new Vector2Int(requestedWidth, requestedHeight);
			}

			return new Vector2Int(
				Mathf.Max(16, Mathf.FloorToInt(requestedWidth * scale)),
				Mathf.Max(16, Mathf.FloorToInt(requestedHeight * scale)));
		}
	}
}
```

### ShotTaskExecutor.cs

Конечный автомат: координатор вызывает `Tick()` из `OnUpdate`, пока `IsCompleted` не станет `true`. Окно живёт ровно один шот и всегда закрывается.

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

		private const double SettleSeconds = 0.5d;

		private readonly TaskContext _context;
		private readonly List<SceneShotItem> _items;
		private readonly List<string> _logs = new List<string>();
		private readonly List<string> _summary = new List<string>();
		private readonly HashSet<string> _usedFileNames = new HashSet<string>();

		private int _index;
		private SceneView _window;
		private Vector2Int _targetPx;
		private double _settleUntil;
		private bool _awaitingSettle;
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
			return new ShotTaskExecutor(context, items);
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

		public void Tick()
		{
			if (_completed)
			{
				return;
			}

			if (_context.CancellationToken.IsCancellationRequested)
			{
				CloseWindow();
				_completed = true;
				return;
			}

			try
			{
				if (_awaitingSettle)
				{
					TickSettle();
					return;
				}

				TickPrepare();
			}
			catch (Exception ex)
			{
				string name = _index < _items.Count ? _items[_index].Name : "<unknown>";
				_logs.Add("shot '" + name + "': " + ex.GetBaseException().Message);
				_status = "runtime_error";
				CloseWindow();
				_completed = true;
			}
		}

		private void TickPrepare()
		{
			if (_index >= _items.Count)
			{
				_completed = true;
				return;
			}

			SceneShotItem item = _items[_index];
			SceneShotPose pose = item.Mode == SceneShotPoseMode.Frame
				? SceneShotFramer.Frame(item.FrameTarget, item.FrameMargin, item.FrameRotation, item.Orthographic)
				: item.Pose;

			int ppp = Mathf.Max(1, Mathf.RoundToInt(EditorGUIUtility.pixelsPerPoint));
			Rect workArea = EditorGUIUtility.GetMainWindowPosition();
			_targetPx = SceneShotResolution.Fit(item.Width, item.Height, ppp, workArea);

			if (_targetPx.x != item.Width || _targetPx.y != item.Height)
			{
				_logs.Add("shot '" + item.Name + "': requested " + item.Width + "x" + item.Height
					+ " does not fit the screen, reduced to " + _targetPx.x + "x" + _targetPx.y);
			}

			_window = EditorWindow.CreateWindow<SceneView>();
			_window.titleContent = new GUIContent(WindowTitle);
			_window.drawGizmos = item.Gizmos;
			_window.showGrid = item.Grid;
			SceneViewGrabber.HideOverlays(_window, message => _logs.Add(message));
			_window.LookAt(pose.Pivot, pose.Rotation, pose.Size, pose.Orthographic, true);
			_window.position = new Rect(
				workArea.x + SceneShotResolution.Border / (float)ppp,
				workArea.y + SceneShotResolution.Border / (float)ppp,
				_targetPx.x / (float)ppp,
				_targetPx.y / (float)ppp);
			_window.Repaint();

			_settleUntil = EditorApplication.timeSinceStartup + SettleSeconds;
			_awaitingSettle = true;
		}

		private void TickSettle()
		{
			if (EditorApplication.timeSinceStartup < _settleUntil)
			{
				_window.Repaint();
				return;
			}

			_awaitingSettle = false;
			SceneShotItem item = _items[_index];
			Write(item);
			CloseWindow();
			_index++;
		}

		private void Write(SceneShotItem item)
		{
			Texture2D texture = SceneViewGrabber.Grab(_window, _targetPx.x, _targetPx.y);

			try
			{
				Directory.CreateDirectory(_context.ArtifactsDirectory);
				string fullPath = Path.Combine(_context.ArtifactsDirectory, BuildFileName(item.Name));
				File.WriteAllBytes(fullPath, texture.EncodeToPNG());

				_context.AddArtifact(fullPath);
				_summary.Add("shot " + item.Name + " -> " + fullPath + " (" + _targetPx.x + "x" + _targetPx.y + ")");
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(texture);
			}
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

	try
	{
		_activeShotExecutor = Shot.ShotTaskExecutor.Begin(payloadPath, _activeShotContext);
	}
	catch (Exception ex)
	{
		_activeShotContext = null;
		FinishTask("rejected", null, new List<string> { ex.GetBaseException().Message }, false);
	}
}

private static void PollShotExecutor()
{
	if (_activeRecord == null)
	{
		return;
	}

	_activeShotExecutor.Tick();

	if (!_activeShotExecutor.IsCompleted)
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

- В `OnBeforeAssemblyReload()` перед записью `interrupted_by_domain_reload` добавить закрытие окна:

```csharp
Shot.ShotTaskExecutor.CloseOrphanWindows();
```

- `RequiresScenePreflight` не менять — `shot` в список не входит.

При таймауте и отмене `CheckTimeout`/`CancelActive` отменяют токен и завершают запись; следующий `Tick()` увидит отменённый токен и закроет окно. Дополнительно окна подчищает `CloseOrphanWindows` при старте моста.

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
- `width`/`height` — дефолт 1280x720, потолок 1920x1080. Если экран меньше, размер пропорционально уменьшается — фактический указан в `ReturnValue`, факт уменьшения в `Logs`.
- `gizmos` — дефолт `true` (иконки компонентов, гизмо). `grid` — дефолт `false`.
- Снимок делается перерисовкой окна в текстуру, а не с экрана: перекрытие окна Unity, потеря фокуса и свёрнутый редактор на результат не влияют.
- Пути готовых PNG приходят в поле `Artifacts` результата — читай их обычным просмотром изображений.
- Снимается текущая открытая сцена. Нужна другая — сначала открой её отдельным `csharp`-таском.
```

## Проверка

- Пересобрать CLI (`dotnet build AgentBridgeCli -c Release`), убедиться что `agentbridge --version` печатает 1.8.0.
- В открытом Unity-проекте выполнить `agentbridge compile` — пакет собирается без ошибок.
- Смоук: создать `Temp/AgentBridge/Task_shot_smoke.shot.json` с одним `frame`-шотом на объект сцены и одним `pose`-шотом, выполнить `agentbridge shot`, убедиться: `Status == "success"`, оба PNG существуют по путям из `Artifacts`, изображения не перевёрнуты, цвета совпадают с реальным Scene View, редакторских оверлеев нет, гизмо и иконки на месте.
- Размер: запросить 1920×1080 на экране меньше этого — убедиться, что в `Logs` есть строка про уменьшение, PNG уменьшен пропорционально и соотношение сторон сохранено.
- Фон: запустить смоук ещё раз, свернув Unity сразу после старта таска — результат должен совпасть с прогоном при видимом редакторе.
- Ошибка: указать несуществующий `frame.target`, убедиться что `Status == "runtime_error"` и временное окно SceneView не осталось открытым.

## После выполнения

- Измени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию проекта (README, шаблоны UNITYAGENT) под новую команду.

## Правки после реализации

По итогам ревью имена разведены, чтобы `shot` сцены и `shot` UI-префаба не путались:

- команда `shot` → `sceneshot`, payload `<TaskId>.shot.json` → `<TaskId>.sceneshot.json`, kind `"sceneshot"`;
- Unity-сторона: `Editor/Shot/` → `Editor/SceneShot/`, namespace `AgentBridge.Shot` → `AgentBridge.SceneShot`, `ShotTaskExecutor` → `SceneShotTaskExecutor`, `ShotPayloadParser` → `SceneShotPayloadParser`;
- действие `shot` в `.ui.json` → `uishot`, старое имя оставлено молчаливым алиасом в `UiTaskRunner`.

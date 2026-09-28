using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgentBridge.SceneShot
{
	// Executes a declarative <TaskId>.sceneshot.json task. Each shot gets its own
	// temporary SceneView window: the view is posed, given a moment to finish
	// laying out and painting, grabbed into a RenderTexture and closed again.
	// The settle wait spans several editor ticks, so the coordinator drives this
	// as a state machine through Tick() instead of getting a result synchronously.
	public class SceneShotTaskExecutor
	{
		public const string WindowTitle = "AgentBridge Scene Shot";

		private const double SettleSeconds = 0.5d;
		private const double GameShotTimeoutSeconds = 5d;

		private readonly TaskContext _context;
		private readonly List<SceneShotItem> _items;
		private readonly string _prefabPath;
		private readonly List<string> _logs = new List<string>();
		private readonly List<string> _summary = new List<string>();
		private readonly HashSet<string> _usedFileNames = new HashSet<string>();

		private int _index;
		private SceneView _window;
		private Vector2Int _targetPx;
		private int _headerPx;
		private double _settleUntil;
		private bool _awaitingSettle;
		private bool _awaitingGameFile;
		private string _gameFilePath;
		private double _gameDeadline;
		private long _gameLastLength;
		private bool _completed;
		private string _status = "success";
		private bool _stagePrepared;
		private bool _restoreStage;
		private string _previousStagePath;

		private SceneShotTaskExecutor(TaskContext context, List<SceneShotItem> items, string prefabPath)
		{
			_context = context;
			_items = items;
			_prefabPath = prefabPath;
			_stagePrepared = string.IsNullOrEmpty(prefabPath);
		}

		public bool IsCompleted
		{
			get { return _completed; }
		}

		public static SceneShotTaskExecutor Begin(string payloadPath, TaskContext context)
		{
			string prefabPath;
			List<SceneShotItem> items = SceneShotPayloadParser.Parse(File.ReadAllText(payloadPath), out prefabPath);
			return new SceneShotTaskExecutor(context, items, prefabPath);
		}

		// A domain reload, a timeout or a crash can leave the temporary window
		// behind; it is recognizable by its title and closed on the next start.
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

			TickStep();

			// Every way out — last shot, failed shot, cancellation — hands the editor back
			// in the stage it was in before the task.
			if (_completed)
			{
				RestoreStage();
			}
		}

		// The coordinator drops an executor without ticking it again on timeout and
		// cancellation. The window and the stage are cleaned up here; stage switching is
		// skipped while assemblies reload, the reloaded editor keeps whatever stage is open.
		public void Abandon(bool restoreStage)
		{
			CloseWindow();
			if (!restoreStage)
			{
				return;
			}

			int logged = _logs.Count;
			RestoreStage();
			for (int i = logged; i < _logs.Count; i++)
			{
				Debug.Log("[AgentBridge] " + _logs[i]);
			}
		}

		private void TickStep()
		{
			if (_context.CancellationToken.IsCancellationRequested)
			{
				CloseWindow();
				_completed = true;
				return;
			}

			try
			{
				if (_awaitingGameFile)
				{
					TickGameFile();
					return;
				}

				if (_awaitingSettle)
				{
					TickSettle();
					return;
				}

				if (!_stagePrepared)
				{
					PrepareStage();
					return;
				}

				TickPrepare();
			}
			catch (Exception ex)
			{
				string name = _index < _items.Count ? _items[_index].Name : "<unknown>";
				_logs.Add((_stagePrepared ? "shot '" + name + "': " : "prefab '" + _prefabPath + "': ")
					+ ex.GetBaseException().Message);
				_status = "runtime_error";
				CloseWindow();
				_completed = true;
			}
		}

		// Opened a tick ahead of the first shot, so the stage scene and the environment Unity
		// builds around the prefab exist before the first Scene View window syncs to it.
		private void PrepareStage()
		{
			PrefabStage current = PrefabStageUtility.GetCurrentPrefabStage();
			_previousStagePath = current != null ? current.assetPath : null;

			PrefabStage stage = AgentSceneManager.OpenPrefab(_prefabPath);
			_stagePrepared = true;

			if (current != null && current == stage)
			{
				_logs.Add("prefab stage " + _prefabPath + " was already open");
				return;
			}

			_restoreStage = true;
			FocusGuard.BeginWindowGuard();
			_logs.Add("opened prefab stage " + _prefabPath);
		}

		private void RestoreStage()
		{
			if (!_restoreStage)
			{
				return;
			}

			_restoreStage = false;

			try
			{
				if (string.IsNullOrEmpty(_previousStagePath))
				{
					AgentSceneManager.ClosePrefab();
					_logs.Add("closed prefab stage " + _prefabPath);
				}
				else
				{
					AgentSceneManager.OpenPrefab(_previousStagePath);
					_logs.Add("reopened prefab stage " + _previousStagePath);
				}
			}
			catch (Exception ex)
			{
				_logs.Add("prefab stage " + _prefabPath + " was left open: " + ex.GetBaseException().Message);
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
			if (item.View == "game")
			{
				PrepareGameShot(item);
				return;
			}

			SceneShotPose pose = item.Mode == SceneShotPoseMode.Frame
				? SceneShotFramer.Frame(item.FrameTarget, item.FrameMargin, item.FrameRotation, item.Orthographic)
				: item.Pose;

			_window = ScriptableObject.CreateInstance<SceneView>();

			int ppp = Mathf.Max(1, Mathf.RoundToInt(EditorGUIUtility.pixelsPerPoint));
			Rect workArea = EditorGUIUtility.GetMainWindowPosition();
			float headerPoints = StageUtility.GetCurrentStage() is MainStage ? 0f : SceneViewGrabber.StageHeaderPoints(_window);
			_headerPx = Mathf.CeilToInt(headerPoints * ppp);
			Rect fitArea = new Rect(workArea.x, workArea.y, workArea.width, Mathf.Max(0f, workArea.height - headerPoints));
			_targetPx = SceneShotResolution.Fit(item.Width, item.Height, ppp, fitArea);

			if (_targetPx.x != item.Width || _targetPx.y != item.Height)
			{
				_logs.Add("shot '" + item.Name + "': requested " + item.Width + "x" + item.Height
					+ " does not fit the screen, reduced to " + _targetPx.x + "x" + _targetPx.y);
			}

			_window.titleContent = new GUIContent(WindowTitle);
			_window.drawGizmos = item.Gizmos;
			_window.showGrid = item.Grid;
			SceneViewGrabber.HideOverlays(_window, message => _logs.Add(message));
			_window.LookAt(pose.Pivot, pose.Rotation, pose.Size, pose.Orthographic, true);
			UnfocusedWindowShower.TryShow(
				_window,
				new Rect(
					workArea.x + SceneShotResolution.Border / (float)ppp,
					workArea.y + SceneShotResolution.Border / (float)ppp,
					_targetPx.x / (float)ppp,
					(_targetPx.y + _headerPx) / (float)ppp),
				message => _logs.Add(message));
			FocusGuard.BeginWindowGuard();
			_window.Repaint();

			_settleUntil = EditorApplication.timeSinceStartup + SettleSeconds;
			_awaitingSettle = true;
		}

		// A game view shot is whatever the player actually sees, overlay UI included, so it is
		// taken by the runtime capture API instead of a posed scene view. That API writes the
		// file on a later frame, hence the wait state below.
		private void PrepareGameShot(SceneShotItem item)
		{
			if (!EditorApplication.isPlaying)
			{
				_logs.Add("shot '" + item.Name + "': game view shot requires play mode (use agentbridge play)");
				_status = "runtime_error";
				_completed = true;
				return;
			}

			try
			{
				Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
				if (gameViewType == null)
				{
					_logs.Add("shot '" + item.Name + "': could not open the Game View: UnityEditor.GameView is not available");
				}
				else if (Resources.FindObjectsOfTypeAll(gameViewType).Length == 0)
				{
					Rect workArea = EditorGUIUtility.GetMainWindowPosition();
					EditorWindow gameView = (EditorWindow)ScriptableObject.CreateInstance(gameViewType);
					UnfocusedWindowShower.TryShow(
						gameView,
						new Rect(workArea.x, workArea.y, 480f, 854f),
						message => _logs.Add(message));
				}
			}
			catch (Exception ex)
			{
				_logs.Add("shot '" + item.Name + "': could not open the Game View: " + ex.GetBaseException().Message);
			}

			FocusGuard.BeginWindowGuard();

			_logs.Add("shot '" + item.Name + "': game view shots use the Game View resolution, requested size ignored");

			Directory.CreateDirectory(_context.ArtifactsDirectory);
			_gameFilePath = Path.Combine(_context.ArtifactsDirectory, BuildFileName(item.Name));
			if (File.Exists(_gameFilePath))
			{
				File.Delete(_gameFilePath);
			}

			ScreenCapture.CaptureScreenshot(_gameFilePath, 1);
			_gameDeadline = EditorApplication.timeSinceStartup + GameShotTimeoutSeconds;
			_gameLastLength = -1;
			_awaitingGameFile = true;
		}

		// CaptureScreenshot returns immediately and the PNG appears a frame or more later, so
		// the file is only accepted once its size stopped growing between two ticks.
		private void TickGameFile()
		{
			SceneShotItem item = _items[_index];

			if (File.Exists(_gameFilePath))
			{
				long length = new FileInfo(_gameFilePath).Length;
				if (length > 0 && length == _gameLastLength)
				{
					_awaitingGameFile = false;
					_context.AddArtifact(_gameFilePath);
					_summary.Add("gameshot " + item.Name + " -> " + _gameFilePath);
					_index++;
					return;
				}

				_gameLastLength = length;
			}

			if (EditorApplication.timeSinceStartup < _gameDeadline)
			{
				return;
			}

			_awaitingGameFile = false;
			_logs.Add("shot '" + item.Name + "': game view capture timed out (is a Game View rendering?)");
			_status = "runtime_error";
			_completed = true;
		}

		// A freshly created window needs several paints before its layout and the
		// scene rendering inside it are final, so keep asking for repaints until
		// the settle window elapses and only then read the view.
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
			Texture2D texture = SceneViewGrabber.Grab(_window, _targetPx.x, _targetPx.y + _headerPx);
			if (_headerPx > 0)
			{
				Texture2D full = texture;
				texture = SceneViewGrabber.CropTop(full, _headerPx);
				UnityEngine.Object.DestroyImmediate(full);
			}

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

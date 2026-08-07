using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
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

		private SceneShotTaskExecutor(TaskContext context, List<SceneShotItem> items)
		{
			_context = context;
			_items = items;
		}

		public bool IsCompleted
		{
			get { return _completed; }
		}

		public static SceneShotTaskExecutor Begin(string payloadPath, TaskContext context)
		{
			List<SceneShotItem> items = SceneShotPayloadParser.Parse(File.ReadAllText(payloadPath));
			return new SceneShotTaskExecutor(context, items);
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

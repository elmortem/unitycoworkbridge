using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.SceneShot
{
	// Grabs the pixels of an EditorWindow by forcing its view to repaint into a
	// RenderTexture instead of reading the screen. That keeps the capture immune to
	// the editor being occluded, unfocused or minimized, and still renders gizmos,
	// component icons and the grid. The API lives on the internal GUIView behind
	// EditorWindow.m_Parent, so it is reached by reflection and cached.
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

			// A render target in the wrong color space washes the image out on
			// linear projects, so it has to follow the project setting.
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

		// GrabPixels writes the view bottom-up, PNG expects top-down.
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

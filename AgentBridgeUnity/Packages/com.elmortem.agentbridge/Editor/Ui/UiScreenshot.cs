using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AgentBridge.Ui
{
	// Renders a UI prefab offscreen to a PNG, writes a sidecar rects.json with
	// every node's screen rect, and draws colored outlines for requested paths.
	public static class UiScreenshot
	{
		private static readonly string[] Palette =
		{
			"#FF3B30", "#34C759", "#0A84FF", "#FFD60A", "#BF5AF2", "#FF9F0A", "#64D2FF", "#FF375F"
		};

		public static string Shot(string prefabPath, string outputPng, int width, int height, List<string> outlinePaths)
		{
			UiStage stage = UiPrefabStage.Open(prefabPath, width, height, true);
			try
			{
				stage.Camera.Render();
				stage.Camera.Render();

				RenderTexture previous = RenderTexture.active;
				RenderTexture.active = stage.Texture;
				var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
				texture.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0);
				texture.Apply();
				RenderTexture.active = previous;

				var nodes = new List<object>();
				foreach (var rt in stage.Instance.GetComponentsInChildren<RectTransform>(true))
				{
					UnityEngine.Rect sr = UiPrefabStage.ScreenRect(stage, rt);
					nodes.Add(new Dictionary<string, object>
					{
						{ "path", UiPath.PathOf(rt, stage.Instance.transform) },
						{ "rect", new List<object> { sr.x, sr.y, sr.width, sr.height } }
					});
				}

				var outlines = new List<object>();
				if (outlinePaths != null)
				{
					for (int i = 0; i < outlinePaths.Count; i++)
					{
						string path = outlinePaths[i];
						Transform node = UiPath.Resolve(stage.Instance.transform, path);
						if (!(node is RectTransform rt))
							continue;

						Color color = ParseColor(Palette[i % Palette.Length]);
						UnityEngine.Rect sr = UiPrefabStage.ScreenRect(stage, rt);
						DrawOutline(texture, sr, color, width, height);
						outlines.Add(new Dictionary<string, object>
						{
							{ "path", path },
							{ "color", Palette[i % Palette.Length] }
						});
					}
				}

				texture.Apply();
				byte[] png = texture.EncodeToPNG();
				File.WriteAllBytes(outputPng, png);
				UnityEngine.Object.DestroyImmediate(texture);

				var rectsDoc = new Dictionary<string, object>
				{
					{ "reference", new List<object> { (double)width, (double)height } },
					{ "nodes", nodes },
					{ "outlines", outlines }
				};
				File.WriteAllText(outputPng + ".rects.json", UiJson.Write(rectsDoc));

				return outputPng;
			}
			finally
			{
				UiPrefabStage.Close(stage);
			}
		}

		private static void DrawOutline(Texture2D texture, UnityEngine.Rect rect, Color color, int width, int height)
		{
			int x0 = Mathf.RoundToInt(rect.x);
			int y0 = Mathf.RoundToInt(rect.y);
			int x1 = Mathf.RoundToInt(rect.x + rect.width);
			int y1 = Mathf.RoundToInt(rect.y + rect.height);
			const int thickness = 2;

			for (int k = 0; k < thickness; k++)
			{
				HorizontalLine(texture, x0, x1, y0 + k, color, width, height);
				HorizontalLine(texture, x0, x1, y1 - 1 - k, color, width, height);
				VerticalLine(texture, y0, y1, x0 + k, color, width, height);
				VerticalLine(texture, y0, y1, x1 - 1 - k, color, width, height);
			}
		}

		private static void HorizontalLine(Texture2D texture, int xStart, int xEnd, int yImg, Color color, int width, int height)
		{
			for (int x = xStart; x < xEnd; x++)
				SetPixel(texture, x, yImg, color, width, height);
		}

		private static void VerticalLine(Texture2D texture, int yStart, int yEnd, int xImg, Color color, int width, int height)
		{
			for (int y = yStart; y < yEnd; y++)
				SetPixel(texture, xImg, y, color, width, height);
		}

		private static void SetPixel(Texture2D texture, int xImg, int yImg, Color color, int width, int height)
		{
			if (xImg < 0 || xImg >= width || yImg < 0 || yImg >= height)
				return;

			int yTex = height - 1 - yImg;
			texture.SetPixel(xImg, yTex, color);
		}

		private static Color ParseColor(string hex)
		{
			ColorUtility.TryParseHtmlString(hex, out Color color);
			return color;
		}
	}
}

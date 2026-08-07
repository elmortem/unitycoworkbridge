using UnityEngine;

namespace AgentBridge.SceneShot
{
	// The Scene View window is a real OS window, so the capture cannot exceed the
	// desktop work area: a larger RenderTexture only adds garbage around the
	// natively sized content. An oversized request is therefore scaled down by a
	// single factor on both axes — a per-axis clamp would change the aspect ratio
	// and with it the framing the agent asked for.
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

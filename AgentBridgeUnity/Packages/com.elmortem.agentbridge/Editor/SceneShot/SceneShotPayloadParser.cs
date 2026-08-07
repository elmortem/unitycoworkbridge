using System;
using System.Collections.Generic;
using UnityEngine;
using AgentBridge.Ui;

namespace AgentBridge.SceneShot
{
	// Parses a declarative <TaskId>.sceneshot.json payload into scene shot items.
	public static class SceneShotPayloadParser
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

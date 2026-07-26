using System;
using System.Collections.Generic;
using UnityEngine;

namespace AgentBridge.Ui
{
	// Node identity in a UI prefab is the path from the root by names.
	// Duplicate sibling names are disambiguated with an index: Name[2] is the
	// third child called Name (0-based, so Name == Name[0]).
	public static class UiPath
	{
		public static Transform Resolve(Transform root, string path)
		{
			if (string.IsNullOrEmpty(path))
				return root;

			Transform current = root;
			foreach (string segment in path.Split('/'))
			{
				if (segment.Length == 0)
					continue;

				ParseSegment(segment, out string name, out int index);
				current = FindChild(current, name, index);
				if (current == null)
					return null;
			}

			return current;
		}

		public static Transform ResolveOrCreate(Transform root, string path, List<string> log)
		{
			if (string.IsNullOrEmpty(path))
				return root;

			Transform current = root;
			foreach (string segment in path.Split('/'))
			{
				if (segment.Length == 0)
					continue;

				ParseSegment(segment, out string name, out int index);
				Transform child = FindChild(current, name, index);
				if (child == null)
				{
					if (segment.IndexOf('[') >= 0)
						throw new Exception("Cannot create indexed path segment '" + segment + "' — the node does not exist");

					var go = new GameObject(name, typeof(RectTransform));
					go.transform.SetParent(current, false);
					if (log != null)
						log.Add("Created node: " + name);
					child = go.transform;
				}

				current = child;
			}

			return current;
		}

		public static string PathOf(Transform t, Transform root)
		{
			if (t == root || t == null)
				return "";

			var segments = new List<string>();
			Transform current = t;
			while (current != null && current != root)
			{
				segments.Add(SegmentOf(current));
				current = current.parent;
			}

			segments.Reverse();
			return string.Join("/", segments);
		}

		private static string SegmentOf(Transform t)
		{
			Transform parent = t.parent;
			if (parent == null)
				return t.name;

			int sameNameCount = 0;
			int index = 0;
			for (int i = 0; i < parent.childCount; i++)
			{
				Transform sibling = parent.GetChild(i);
				if (sibling.name != t.name)
					continue;

				if (sibling == t)
					index = sameNameCount;
				sameNameCount++;
			}

			if (sameNameCount > 1)
				return t.name + "[" + index + "]";

			return t.name;
		}

		private static Transform FindChild(Transform parent, string name, int index)
		{
			int seen = 0;
			for (int i = 0; i < parent.childCount; i++)
			{
				Transform child = parent.GetChild(i);
				if (child.name != name)
					continue;

				if (seen == index)
					return child;
				seen++;
			}

			return null;
		}

		private static void ParseSegment(string segment, out string name, out int index)
		{
			index = 0;
			int open = segment.IndexOf('[');
			if (open < 0)
			{
				name = segment;
				return;
			}

			int close = segment.IndexOf(']', open);
			if (close < 0)
				throw new Exception("Malformed path segment '" + segment + "'");

			name = segment.Substring(0, open);
			string indexText = segment.Substring(open + 1, close - open - 1);
			if (!int.TryParse(indexText, out index))
				throw new Exception("Malformed index in path segment '" + segment + "'");
		}
	}
}

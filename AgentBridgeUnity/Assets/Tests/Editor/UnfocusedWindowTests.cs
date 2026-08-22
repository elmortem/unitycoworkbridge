using AgentBridge;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class UnfocusedWindowTests
{
	[Test]
	public void TryShow_DoesNotFocusWindow()
	{
		SceneView view = ScriptableObject.CreateInstance<SceneView>();

		try
		{
			bool shown = UnfocusedWindowShower.TryShow(view, new Rect(100f, 100f, 300f, 200f), _ => { });

			Assert.IsTrue(shown);
			Assert.AreNotEqual(view, EditorWindow.focusedWindow);
		}
		finally
		{
			view.Close();
		}
	}
}

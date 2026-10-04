using System.Collections.Generic;
using AgentBridge.Ui;
using NUnit.Framework;
using TMPro;
using UnityEngine;

public class UiTextWrappingTests
{
	[Test]
	public void WrapTogglesOnExistingTextAndOmissionPreservesIt()
	{
		var go = new GameObject("Wrapping probe", typeof(RectTransform));
		try
		{
			var text = go.AddComponent<TextMeshProUGUI>();
			var fragment = new Dictionary<string, object> { { "type", "TextMeshProUGUI" } };
			foreach (bool wrap in new[] { false, true, false })
			{
				fragment["wrap"] = wrap;
				UiComponentSync.Sync(go, fragment, new UiRefQueue(), null);
				AssertWrap(text, wrap);
				fragment.Remove("wrap");
				fragment["text"] = "Updated text";
				UiComponentSync.Sync(go, fragment, new UiRefQueue(), null);
				AssertWrap(text, wrap);
				Assert.AreSame(text, go.GetComponent<TextMeshProUGUI>());
			}
		}
		finally
		{
			Object.DestroyImmediate(go);
		}
	}

	private static void AssertWrap(TMP_Text text, bool expected)
	{
		var mode = typeof(TMP_Text).GetProperty("textWrappingMode");
		if (mode != null)
			Assert.AreEqual(expected ? "Normal" : "NoWrap", mode.GetValue(text).ToString());
		else
			Assert.AreEqual(expected, typeof(TMP_Text).GetProperty("enableWordWrapping").GetValue(text));
	}
}

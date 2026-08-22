using System;

namespace AgentBridge.Ui
{
	// Resolves a component type name from a ui task: short aliases first, then
	// any type deriving from Component.
	public static class UiComponentTypes
	{
		public static Type Resolve(string typeName)
		{
			switch (typeName)
			{
				case "Image": return typeof(UnityEngine.UI.Image);
				case "Text": return typeof(TMPro.TextMeshProUGUI);
				case "Button": return typeof(UnityEngine.UI.Button);
			}

			return DomainTypeResolver.FindComponentType(typeName);
		}
	}
}

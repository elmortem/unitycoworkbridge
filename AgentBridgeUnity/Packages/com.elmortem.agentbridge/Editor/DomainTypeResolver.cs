using System;
using System.Collections.Generic;
using System.Reflection;

namespace AgentBridge
{
	public static class DomainTypeResolver
	{
		public static Type FindType(string className)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(className);
				if (type != null)
				{
					return type;
				}
			}

			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch
				{
					continue;
				}

				foreach (Type type in types)
				{
					if (type.Name == className)
					{
						return type;
					}
				}
			}

			return null;
		}

		public static Type FindComponentType(string className)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type type = assembly.GetType(className);
				if (type != null && typeof(UnityEngine.Component).IsAssignableFrom(type))
				{
					return type;
				}
			}

			var matches = new List<Type>();
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch
				{
					continue;
				}

				foreach (Type type in types)
				{
					if (type.Name == className && typeof(UnityEngine.Component).IsAssignableFrom(type) && !matches.Contains(type))
					{
						matches.Add(type);
					}
				}
			}

			if (matches.Count == 0)
			{
				return null;
			}

			if (matches.Count > 1)
			{
				var names = new List<string>();
				foreach (Type match in matches)
				{
					names.Add(match.FullName);
				}

				throw new Exception("Ambiguous component type '" + className + "': " + string.Join(", ", names) + ". Use the full type name.");
			}

			return matches[0];
		}
	}
}

using System;
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
	}
}

using System;
using System.Reflection;

namespace AgentBridge
{
	public static class RoslynReflectionHelper
	{
		public static MethodInfo FindBestOverload(Type type, string methodName, BindingFlags flags, Type firstParamType)
		{
			MethodInfo chosen = null;

			foreach (MethodInfo candidate in type.GetMethods(flags))
			{
				if (candidate.Name != methodName)
				{
					continue;
				}

				ParameterInfo[] parameters = candidate.GetParameters();
				if (parameters.Length == 0 || parameters[0].ParameterType != firstParamType)
				{
					continue;
				}

				if (chosen == null || parameters.Length < chosen.GetParameters().Length)
				{
					chosen = candidate;
				}
			}

			return chosen;
		}

		public static object[] BuildArgsWithDefaults(MethodInfo method, params object[] providedLeading)
		{
			ParameterInfo[] parameters = method.GetParameters();
			var args = new object[parameters.Length];

			for (int i = 0; i < parameters.Length; i++)
			{
				if (i < providedLeading.Length)
				{
					args[i] = providedLeading[i];
					continue;
				}

				ParameterInfo parameter = parameters[i];
				if (parameter.HasDefaultValue)
				{
					args[i] = parameter.DefaultValue;
				}
				else if (parameter.ParameterType.IsValueType)
				{
					args[i] = Activator.CreateInstance(parameter.ParameterType);
				}
				else
				{
					args[i] = null;
				}
			}

			return args;
		}

		public static object CreateGenericList(Type elementType)
		{
			Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(elementType);
			return Activator.CreateInstance(listType);
		}

		public static object[] BuildArgs(MethodBase method, object firstPositional, System.Collections.Generic.IDictionary<string, object> namedOverrides)
		{
			ParameterInfo[] parameters = method.GetParameters();
			var args = new object[parameters.Length];

			if (parameters.Length > 0)
			{
				args[0] = firstPositional;
			}

			for (int i = 1; i < parameters.Length; i++)
			{
				ParameterInfo parameter = parameters[i];
				object value;
				if (namedOverrides != null && namedOverrides.TryGetValue(parameter.Name, out value))
				{
					args[i] = value;
					continue;
				}

				if (parameter.HasDefaultValue)
				{
					args[i] = parameter.DefaultValue;
				}
				else if (parameter.ParameterType.IsValueType)
				{
					args[i] = Activator.CreateInstance(parameter.ParameterType);
				}
				else
				{
					args[i] = null;
				}
			}

			return args;
		}

		public static object GetStaticMember(Type type, string name)
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
			if (property != null)
			{
				return property.GetValue(null, null);
			}

			FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
			if (field != null)
			{
				return field.GetValue(null);
			}

			return null;
		}

		public static ConstructorInfo FindConstructorWithParameterName(Type type, string paramName)
		{
			foreach (ConstructorInfo ctor in type.GetConstructors())
			{
				foreach (ParameterInfo parameter in ctor.GetParameters())
				{
					if (parameter.Name == paramName)
					{
						return ctor;
					}
				}
			}

			return null;
		}

		public static object[] BuildArgsAllNamed(MethodBase method, System.Collections.Generic.IDictionary<string, object> namedOverrides)
		{
			ParameterInfo[] parameters = method.GetParameters();
			var args = new object[parameters.Length];

			for (int i = 0; i < parameters.Length; i++)
			{
				ParameterInfo parameter = parameters[i];
				object value;
				if (namedOverrides != null && namedOverrides.TryGetValue(parameter.Name, out value))
				{
					args[i] = value;
					continue;
				}

				if (parameter.HasDefaultValue)
				{
					args[i] = parameter.DefaultValue;
				}
				else if (parameter.ParameterType.IsValueType)
				{
					args[i] = Activator.CreateInstance(parameter.ParameterType);
				}
				else
				{
					args[i] = null;
				}
			}

			return args;
		}

		public static ConstructorInfo FindBestConstructor(Type type, Type firstParamType)
		{
			ConstructorInfo chosen = null;

			foreach (ConstructorInfo candidate in type.GetConstructors())
			{
				ParameterInfo[] parameters = candidate.GetParameters();
				if (parameters.Length == 0 || parameters[0].ParameterType != firstParamType)
				{
					continue;
				}

				if (chosen == null || parameters.Length < chosen.GetParameters().Length)
				{
					chosen = candidate;
				}
			}

			return chosen;
		}
	}
}

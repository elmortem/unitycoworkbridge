using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace AgentBridge
{
	// UnityEditor.EditorDialogEvents (Unity 6.7+) raises dialogWillShow just before a native message
	// box or a ShowModal window takes the main thread, and dialogDismissed when it lets go. The
	// package compiles against 2022.3, so the events are bound through reflection; older editors
	// simply have no such type and rely on NativeDialogProbe.
	internal static class EditorDialogEventsHook
	{
		private const string TypeName = "UnityEditor.EditorDialogEvents";

		private static EventInfo _willShowEvent;
		private static EventInfo _dismissedEvent;
		private static Delegate _willShowHandler;
		private static Delegate _dismissedHandler;
		private static Action<EditorDialogInfo> _onShow;
		private static Action<string> _onDismiss;
		private static string _error;

		public static bool Installed { get { return _willShowHandler != null; } }

		public static string Error { get { return _error; } }

		public static bool TypeAvailable { get { return FindType() != null; } }

		public static bool TryInstall(Action<EditorDialogInfo> onShow, Action<string> onDismiss)
		{
			Uninstall();
			try
			{
				Type type = FindType();
				if (type == null)
				{
					return false;
				}

				EventInfo willShow = type.GetEvent("dialogWillShow", BindingFlags.Public | BindingFlags.Static);
				EventInfo dismissed = type.GetEvent("dialogDismissed", BindingFlags.Public | BindingFlags.Static);
				if (willShow == null || dismissed == null)
				{
					_error = "EditorDialogEvents has no dialogWillShow/dialogDismissed";
					return false;
				}

				Delegate showHandler = CreateHandler(willShow.EventHandlerType, "OnWillShow");
				Delegate dismissHandler = CreateHandler(dismissed.EventHandlerType, "OnDismissed");
				if (showHandler == null || dismissHandler == null)
				{
					_error = "unexpected EditorDialogEvents handler type " + willShow.EventHandlerType;
					return false;
				}

				_onShow = onShow;
				_onDismiss = onDismiss;
				willShow.AddEventHandler(null, showHandler);
				try
				{
					dismissed.AddEventHandler(null, dismissHandler);
				}
				catch
				{
					willShow.RemoveEventHandler(null, showHandler);
					throw;
				}

				_willShowEvent = willShow;
				_dismissedEvent = dismissed;
				_willShowHandler = showHandler;
				_dismissedHandler = dismissHandler;
				_error = null;
				return true;
			}
			catch (Exception exception)
			{
				_error = exception.GetType().Name + ": " + exception.Message;
				return false;
			}
		}

		public static void Uninstall()
		{
			try
			{
				if (_willShowEvent != null && _willShowHandler != null)
				{
					_willShowEvent.RemoveEventHandler(null, _willShowHandler);
				}

				if (_dismissedEvent != null && _dismissedHandler != null)
				{
					_dismissedEvent.RemoveEventHandler(null, _dismissedHandler);
				}
			}
			catch
			{
			}

			_willShowEvent = null;
			_dismissedEvent = null;
			_willShowHandler = null;
			_dismissedHandler = null;
			_onShow = null;
			_onDismiss = null;
		}

		// Field and property names follow DialogEventInfo: Id, Title, Message, Level (DialogIconType?),
		// ButtonLabels, Source (DialogSource), OpenedAtUtc.
		public static EditorDialogInfo Convert(object info)
		{
			object id = Member(info, "Id");
			object source = Member(info, "Source");
			object level = Member(info, "Level");
			object opened = Member(info, "OpenedAtUtc");
			return new EditorDialogInfo
			{
				Id = "unity:" + (id != null ? id.ToString() : Guid.NewGuid().ToString("N")),
				Source = "editor_dialog_events",
				Kind = KindOf(source != null ? source.ToString() : ""),
				Level = level != null ? level.ToString().ToLowerInvariant() : "",
				Title = Member(info, "Title") as string ?? "",
				Message = Member(info, "Message") as string ?? "",
				Buttons = StringsOf(Member(info, "ButtonLabels")),
				OpenedAtUtc = opened is DateTime ? ((DateTime)opened).ToUniversalTime().ToString("o") : DateTime.UtcNow.ToString("o")
			};
		}

		public static string KindOf(string source)
		{
			if (source == "NativeMessageBox")
			{
				return "native_message_box";
			}

			if (source == "ManagedCustomWindow")
			{
				return "managed_custom_window";
			}

			return string.IsNullOrEmpty(source) ? "unknown" : source.ToLowerInvariant();
		}

		private static Type FindType()
		{
			Type type = typeof(EditorApplication).Assembly.GetType(TypeName, false);
			if (type != null)
			{
				return type;
			}

			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string name = assembly.GetName().Name;
				if (name == null || !name.StartsWith("UnityEditor", StringComparison.Ordinal))
				{
					continue;
				}

				type = assembly.GetType(TypeName, false);
				if (type != null)
				{
					return type;
				}
			}

			return null;
		}

		private static Delegate CreateHandler(Type handlerType, string methodName)
		{
			if (handlerType == null || !handlerType.IsGenericType || handlerType.GetGenericTypeDefinition() != typeof(Action<>))
			{
				return null;
			}

			MethodInfo generic = typeof(EditorDialogEventsHook).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
			if (generic == null)
			{
				return null;
			}

			MethodInfo method = generic.MakeGenericMethod(handlerType.GetGenericArguments()[0]);
			return Delegate.CreateDelegate(handlerType, method);
		}

		// Unity calls these right before and after its own modal loop: nothing may escape into it.
		private static void OnWillShow<T>(T info)
		{
			try
			{
				Action<EditorDialogInfo> callback = _onShow;
				if (callback != null)
				{
					callback(Convert(info));
				}
			}
			catch (Exception exception)
			{
				_error = exception.GetType().Name + ": " + exception.Message;
			}
		}

		private static void OnDismissed<T>(T info)
		{
			try
			{
				Action<string> callback = _onDismiss;
				if (callback != null)
				{
					object id = Member(info, "Id");
					callback("unity:" + (id != null ? id.ToString() : ""));
				}
			}
			catch (Exception exception)
			{
				_error = exception.GetType().Name + ": " + exception.Message;
			}
		}

		private static object Member(object target, string name)
		{
			if (target == null)
			{
				return null;
			}

			Type type = target.GetType();
			FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
			if (field != null)
			{
				return field.GetValue(target);
			}

			PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
			return property != null && property.GetIndexParameters().Length == 0 ? property.GetValue(target, null) : null;
		}

		private static string[] StringsOf(object value)
		{
			var strings = value as string[];
			if (strings != null)
			{
				return (string[])strings.Clone();
			}

			var items = value as IEnumerable;
			if (items == null || value is string)
			{
				return new string[0];
			}

			var result = new List<string>();
			foreach (object item in items)
			{
				if (item != null)
				{
					result.Add(item.ToString());
				}
			}

			return result.ToArray();
		}
	}
}

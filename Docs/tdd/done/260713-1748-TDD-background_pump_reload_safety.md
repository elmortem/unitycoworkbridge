Status: Выполнено

# ТДД: reload-safe жизненный цикл CoworkBackgroundPump

## Тип

Рефакторинг существующего кода. Фокус — что меняется и что остаётся стабильным, ключевой инвариант потокобезопасности. Публичный контракт (`Start`/`Stop`) и назначение фичи не меняются.

## Проблема

После добавления фоновой обработки задач (`CoworkBackgroundPump`) Unity зависает при старте моста на окне:

```
Hold on (busy for Ns)...
EditorApplication.update: CoworkBridge.CoworkBridge.OnEditorUpdate
Waiting for user code in CoworkBridge.dll to finish executing.
```

Причина: `CoworkBackgroundPump` поднимает управляемый фоновый поток, который остаётся живым во время domain reload. Unity перед перезагрузкой домена ждёт, пока весь managed-код из `CoworkBridge.dll` (в т.ч. код в фоновом потоке) отпустит домен, и показывает это окно. Текущий `Stop()` поток по-настоящему не останавливает.

Дефекты текущей реализации:

- `Stop()` только ставит `_running = false` и обнуляет ссылку. Нет `Join`. Цикл спит в `Thread.Sleep(500)`, поэтому на `beforeAssemblyReload` поток ещё выполняет `Directory.GetFiles`/`File.Exists` из `CoworkBridge.dll`, и reload виснет.
- Поток может утечь и задвоиться через reload: после перезагрузки статики сбрасываются (`_running = false`), и гард `if (_running) return;` в `Start()` не видит поток из предыдущего домена.
- `Directory.GetFiles` по всей папке каждые 200 мс на потоке: если IO залипнет в нативном вызове, Unity не сможет прервать поток вовсе — вечный фриз.

Функциональная предыстория: `EditorApplication.update` перестаёт вызываться, когда окно редактора теряет фокус (штатное поведение Unity). В зрелых проектах редактор продолжает тикать в фоне из-за сторонних перерисовок; в свежесозданных/пустых будить нечем — таски не выполняются без ручного фокуса. `PostMessage` в окно редактора остаётся штатным способом «толкнуть» цикл сообщений — механизм пробуждения сохраняем.

## Инвариант

Ни один управляемый фоновый поток `CoworkBridge` не должен быть жив во время domain reload.

Гарантия инварианта:

- Цикл потока ждёт на отменяемом примитиве (`ManualResetEventSlim`), а не на `Thread.Sleep`, поэтому реагирует на остановку немедленно.
- `Stop()` синхронно останавливает и `Join`-ит поток с таймаутом.
- `Stop()` уже подписан на `AssemblyReloadEvents.beforeAssemblyReload` и `EditorApplication.quitting` (в `CoworkBridge.Initialize`), поэтому поток гарантированно завершается до того, как Unity начнёт перезагрузку домена.

## Область изменений

Меняется один файл: `CoworkBridge/Editor/CoworkBackgroundPump.cs` — переписывается целиком.

`CoworkBridge/Editor/CoworkBridge.cs` не меняется. Проверить (не править), что подписки уже на месте в `Initialize` и `Stop`:

- `AssemblyReloadEvents.beforeAssemblyReload -= CoworkBackgroundPump.Stop; += CoworkBackgroundPump.Stop;`
- `EditorApplication.quitting -= CoworkBackgroundPump.Stop; += CoworkBackgroundPump.Stop;`
- `CoworkBackgroundPump.Start(_coworkPath);` в конце `Initialize`
- `CoworkBackgroundPump.Stop();` в `CoworkBridge.Stop()`

## Что меняется в CoworkBackgroundPump

- Добавляется `ManualResetEventSlim _stopSignal` и константа `JoinTimeoutMs = 2000`.
- `Loop` вместо `Thread.Sleep(waitMs)` ждёт `_stopSignal.Wait(waitMs)`.
- `Stop` становится настоящей синхронной остановкой: `_running = false` → `_stopSignal.Set()` → `Join(JoinTimeoutMs)` → `Dispose` сигнала → обнуление ссылок. Порядок обязателен: `Dispose` только после `Join`, иначе поток обратится к уничтоженному сигналу.
- `Stop` идемпотентен: `if (!_running) return;` в начале.
- Проверка платформы Windows переносится в начало `Start` (до создания сигнала и потока).
- `HasPendingWork`, `IsTaskFile`, `TaskIdOf`, `WakeEditor`, `ResolveWindowHandle` остаются без изменений по логике. `ResolveWindowHandle` продолжает лениво кешировать HWND и самовосстанавливается, если на первом `Start` окно ещё не создано (`Process.MainWindowHandle` — чистый Win32, вызов из фонового потока безопасен).

## Итоговый файл CoworkBackgroundPump.cs

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace CoworkBridge
{
	public static class CoworkBackgroundPump
	{
		private const int PendingIntervalMs = 200;
		private const int IdleIntervalMs = 500;
		private const int JoinTimeoutMs = 2000;
		private const uint WmNull = 0x0000;

		private static Thread _thread;
		private static volatile bool _running;
		private static string _coworkPath;
		private static IntPtr _windowHandle;
		private static ManualResetEventSlim _stopSignal;

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

		public static void Start(string coworkPath)
		{
			_coworkPath = coworkPath;

			if (Application.platform != RuntimePlatform.WindowsEditor)
			{
				return;
			}

			if (_running)
			{
				return;
			}

			_windowHandle = IntPtr.Zero;
			_stopSignal = new ManualResetEventSlim(false);
			_running = true;
			_thread = new Thread(Loop)
			{
				IsBackground = true,
				Name = "CoworkBridgeBackgroundPump"
			};
			_thread.Start();
		}

		public static void Stop()
		{
			if (!_running)
			{
				return;
			}

			_running = false;

			if (_stopSignal != null)
			{
				_stopSignal.Set();
			}

			Thread thread = _thread;
			if (thread != null)
			{
				bool finished = thread.Join(JoinTimeoutMs);
				if (!finished)
				{
					Debug.LogWarning("[CoworkBridge] Background pump did not stop within timeout.");
				}
			}

			if (_stopSignal != null)
			{
				_stopSignal.Dispose();
				_stopSignal = null;
			}

			_thread = null;
		}

		private static void Loop()
		{
			while (_running)
			{
				int waitMs = IdleIntervalMs;

				if (HasPendingWork())
				{
					WakeEditor();
					waitMs = PendingIntervalMs;
				}

				_stopSignal.Wait(waitMs);
			}
		}

		private static bool HasPendingWork()
		{
			try
			{
				if (string.IsNullOrEmpty(_coworkPath) || !Directory.Exists(_coworkPath))
				{
					return false;
				}

				if (File.Exists(Path.Combine(_coworkPath, "clean.command")))
				{
					return true;
				}

				foreach (string path in Directory.GetFiles(_coworkPath))
				{
					if (!IsTaskFile(path))
					{
						continue;
					}

					string taskId = TaskIdOf(path);
					string donePath = Path.Combine(_coworkPath, "result_" + taskId + ".done");
					if (!File.Exists(donePath))
					{
						return true;
					}
				}

				return false;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool IsTaskFile(string path)
		{
			if (path.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
		}

		private static string TaskIdOf(string filePath)
		{
			string name = Path.GetFileName(filePath);
			if (name.EndsWith(".ui.json", StringComparison.OrdinalIgnoreCase))
			{
				return name.Substring(0, name.Length - ".ui.json".Length);
			}

			return Path.GetFileNameWithoutExtension(name);
		}

		private static void WakeEditor()
		{
			IntPtr handle = ResolveWindowHandle();
			if (handle == IntPtr.Zero)
			{
				return;
			}

			PostMessage(handle, WmNull, IntPtr.Zero, IntPtr.Zero);
		}

		private static IntPtr ResolveWindowHandle()
		{
			if (_windowHandle != IntPtr.Zero)
			{
				return _windowHandle;
			}

			try
			{
				_windowHandle = Process.GetCurrentProcess().MainWindowHandle;
			}
			catch (Exception)
			{
				_windowHandle = IntPtr.Zero;
			}

			return _windowHandle;
		}
	}
}
```

## Порядок остановки (обязателен)

- `_running = false` — цикл после текущей итерации выйдет.
- `_stopSignal.Set()` — снимает поток с `Wait` немедленно, без ожидания интервала.
- `Join(JoinTimeoutMs)` — главный поток дожидается фактического завершения фонового.
- `Dispose` сигнала — только после `Join`, иначе поток может обратиться к уничтоженному объекту.
- Обнуление `_thread` — после `Join`.

## Критерии приёмки

- Фриз устранён: старт моста в свежесозданном пустом проекте на Windows не вызывает окно «Waiting for user code in CoworkBridge.dll to finish executing».
- Рекомпиляция скриптов и ручной domain reload при активном мосте проходят без зависания; в консоли нет предупреждения «Background pump did not stop within timeout» при штатной работе.
- Функциональный кейс: в свежесозданном проекте при снятом фокусе с редактора положенный в папку моста таск выполняется без ручного возврата фокуса (в пределах пары секунд).
- Повторный `Stop()` подряд не бросает исключений; повторный `Start()` при уже запущенном потоке не создаёт второй поток.
- На не-Windows редакторе `Start` не создаёт поток и не бросает исключений.

## После выполнения

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить проектную документацию (README / UNITYCOWORK-шаблоны) под изменения.

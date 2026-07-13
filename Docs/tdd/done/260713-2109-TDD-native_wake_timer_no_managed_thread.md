Status: Выполнено

# ТДД: фоновое пробуждение редактора нативным таймером, без managed-потока

## Тип

Рефакторинг. Публичное поведение моста (сканирование задач в `OnEditorUpdate`, работа без фокуса окна) сохраняется. Меняется только механизм пробуждения редактора.

## Проблема

Редактор зависает на старте и при domain reload:

```
Hold on (busy for N)...
EditorApplication.update: CoworkBridge.CoworkBridge.OnEditorUpdate
Waiting for user code in CoworkBridge.dll to finish executing.
```

Механизм: перед domain reload Unity ждёт, пока весь managed-код из `CoworkBridge.dll` перестанет исполняться. Поток `CoworkBackgroundPump` — такой код. Остановка потока через `beforeAssemblyReload` + `Join(2000)` проблему не закрывает:

- Если `Join` таймаутится хотя бы один раз, `Stop()` пишет warning и продолжает — поток утекает в выгружаемый домен. Новый домен остановить его уже не может (статики другие), и каждый последующий reload виснет навечно до перезапуска редактора.
- На стартовых путях редактора (первичная загрузка домена, импорт, немедленная рекомпиляция) поток стартует из `[InitializeOnLoad]` раньше, чем гарантированно отработает цепочка `beforeAssemblyReload`.
- Возможна взаимоблокировка: reload держит главный поток (не качает сообщения), а фоновый поток в этот момент внутри `Process.MainWindowHandle` / IO — `Join` таймаутится, см. первый пункт.

Вывод: любой персистентный managed-поток в editor-сборке — источник этого класса зависаний. Фикс — убрать поток полностью.

## Решение

Редактор без фокуса перестаёт тикать, потому что его message loop спит в `GetMessage`. Любое сообщение в очередь главного окна будит цикл. Вместо потока с `PostMessage(WM_NULL)` ставится нативный Win32-таймер: `SetTimer(hwnd, id, 500, NULL)`. ОС сама кладёт `WM_TIMER` в очередь окна каждые 500 мс — message loop просыпается, редактор тикает, существующий `OnEditorUpdate` (троттлинг 1 с) сканирует папку задач.

Ключевые свойства:

- Никакого managed-кода вне главного потока — Unity при reload ждать нечего, класс бага исчезает архитектурно.
- `WM_TIMER` без TIMERPROC на неизвестный id безвреден для wndproc Unity: сообщение просто будит цикл.
- Повторный `SetTimer` с тем же `hwnd` + `id` заменяет существующий таймер — идемпотентность и самовосстановление после reload без риска дублей.
- Даже если `KillTimer` перед reload не вызовется, «утёкший» таймер не исполняет пользовательский код и переустанавливается новым доменом.

## Область изменений

- Удалить: `CoworkBridge/Editor/CoworkBackgroundPump.cs` и его `.meta`.
- Создать: `CoworkBridge/Editor/CoworkEditorWakeTimer.cs`.
- Править: `CoworkBridge/Editor/CoworkBridge.cs` (только замена ссылок на памп).
- Править: `CoworkBridge/package.json` — версия `0.4.3`.

## Новый файл CoworkEditorWakeTimer.cs

```csharp
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CoworkBridge
{
	public static class CoworkEditorWakeTimer
	{
		private const int TimerId = 0xC0B0;
		private const uint IntervalMs = 500;

		private static IntPtr _windowHandle;
		private static bool _installed;

		[DllImport("user32.dll")]
		private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIdEvent, uint uElapse, IntPtr lpTimerFunc);

		[DllImport("user32.dll")]
		private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIdEvent);

		public static void Start()
		{
			if (Application.platform != RuntimePlatform.WindowsEditor)
			{
				return;
			}

			if (_installed)
			{
				return;
			}

			IntPtr handle = ResolveWindowHandle();
			if (handle == IntPtr.Zero)
			{
				return;
			}

			UIntPtr result = SetTimer(handle, (UIntPtr)TimerId, IntervalMs, IntPtr.Zero);
			_installed = result != UIntPtr.Zero;
		}

		public static void Stop()
		{
			if (!_installed)
			{
				return;
			}

			KillTimer(_windowHandle, (UIntPtr)TimerId);
			_installed = false;
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

Требование к вызовам: `Start()` и `Stop()` вызываются только с главного потока (`SetTimer` привязывает таймер к потоку-владельцу окна). Все точки вызова ниже — главный поток.

## Изменения в CoworkBridge.cs

- В `Initialize()` заменить подписки и запуск:
  - `AssemblyReloadEvents.beforeAssemblyReload -= CoworkBackgroundPump.Stop; += CoworkBackgroundPump.Stop;` → то же самое с `CoworkEditorWakeTimer.Stop`.
  - `EditorApplication.quitting -= CoworkBackgroundPump.Stop; += CoworkBackgroundPump.Stop;` → то же самое с `CoworkEditorWakeTimer.Stop`.
  - `CoworkBackgroundPump.Start(_coworkPath);` → `CoworkEditorWakeTimer.Start();`.
- В `Stop()` (меню Tools/Cowork Bridge/Stop) заменить:
  - отписки `CoworkBackgroundPump.Stop` → `CoworkEditorWakeTimer.Stop` (обе);
  - `CoworkBackgroundPump.Stop();` → `CoworkEditorWakeTimer.Stop();`.
- В `OnEditorUpdate()` первой строкой после троттлинг-гарда (`_lastScanTime = ...`) добавить:

```csharp
CoworkEditorWakeTimer.Start();
```

  Это ленивое самовосстановление: при самом первом запуске редактора `[InitializeOnLoad]` может отработать до создания главного окна (`MainWindowHandle == 0`), тогда таймер поставится с первого тика при активном окне. Вызов дешёвый — гард `_installed` срабатывает сразу.

## Критерии приёмки

- Старт редактора и любой domain reload (рекомпиляция скриптов) при включённом мосте не показывают «Waiting for user code in CoworkBridge.dll to finish executing».
- В свежесозданном пустом проекте на Windows таск, положенный в папку моста при снятом фокусе с редактора, выполняется без возврата фокуса в пределах пары секунд.
- Таск, требующий компиляции (новый .cs), без фокуса проходит полный цикл: компиляция → reload → выполнение → result-файлы.
- После Tools/Cowork Bridge/Stop редактор без фокуса перестаёт тикать (таймер снят); повторный Start снова включает фоновое выполнение.
- Повторные Start/Stop подряд не бросают исключений и не создают дублей таймера.
- На не-Windows редакторе Start — no-op, исключений нет.
- В решении не осталось ссылок на `CoworkBackgroundPump`.

## После выполнения

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить проектную документацию (README / UNITYCOWORK-шаблоны) под изменения.

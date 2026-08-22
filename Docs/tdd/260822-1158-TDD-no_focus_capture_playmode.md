Status: Выполнено

# Бесфокусные скриншоты и вход в плеймод — Agent Execution Spec

Проблема: при плеймод-тестах, `agentbridge play`, sceneshot и gameshot окно Unity вылезает на передний план и крадёт фокус у пользователя. Причины: редактор Unity сам фокусирует Game View при входе в Play Mode; sceneshot создаёт окно через `EditorWindow.CreateWindow<SceneView>()` (фокусирует); gameshot зовёт `GetWindow(gameViewType, false, null, true)` (фокусирует). Решение: показывать служебные окна без активации через internal `EditorWindow.ShowPopupWithMode(ShowMode.Tooltip, false)`, гасить фокусировку Game View при входе в плеймод через internal `PlayModeView.enterPlayModeBehavior = PlayUnfocused`, и Win32-подстраховка, возвращающая фокус предыдущему окну, если Unity всё-таки вылез вперёд.

## References (not inlined)

- Конвенции кода: CLAUDE.md проекта (табы, типы в отдельных файлах, сериализуемые поля public с большой буквы).
- Skills: `unity-bridge` (команды `agentbridge compile`, `agentbridge tests`, `agentbridge sceneshot`, правила про Temp/AgentBridge).
- Факты об API проверены по UnityCsReference ветка 2022.3 (проект на Unity 2022.3.62f2): `EditorWindow.ShowPopupWithMode(ShowMode, bool)` — internal instance метод; `ContainerWindow.ShowPopupWithMode` зовёт `Internal_BringLiveAfterCreation(true, giveFocus, false)` — при `giveFocus=false` окно не активируется; enum `UnityEditor.ShowMode` internal, значение по имени `Tooltip`; `UnityEditor.PlayModeView` internal, свойство `enterPlayModeBehavior`, enum-значение по имени `PlayUnfocused`.

## Foundations (shared, used across units)

Все новые файлы создаются в `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/`:

- `FocusGuardNative.cs` — internal static class `FocusGuardNative`, P/Invoke user32.
- `FocusGuard.cs` — public static class `FocusGuard`, `[InitializeOnLoad]`.
- `UnfocusedWindowShower.cs` — internal static class `UnfocusedWindowShower`.

Правки существующих файлов (тот же каталог): `AgentTestRunner.cs`, `PlaySessionManager.cs`, `SceneShot/SceneShotTaskExecutor.cs`.

Тестовый файл: `AgentBridgeUnity/Assets/Tests/Editor/UnfocusedWindowTests.cs`.

Ключи SessionState (переживают domain reload, очищаются при перезапуске редактора):

- `AgentBridge_FocusGuard_Hwnd` — decimal-строка HWND окна, бывшего на переднем плане до входа; `"0"` = восстанавливать нечего.
- `AgentBridge_FocusGuard_ActiveUntilUtc` — DateTime.UtcNow.ToString("o"), конец окна действия сторожа; отсутствие ключа = сторож неактивен.
- `AgentBridge_FocusGuard_RestoreCount` — int-строка, сколько раз фокус уже возвращали (лимит 2, чтобы не бороться с пользователем, если он сам кликнул в Unity).
- `AgentBridge_FocusGuard_PrevPlayBehavior` — int-строка прежнего значения `enterPlayModeBehavior`; отсутствие = восстанавливать нечего.

Полный код P/Invoke (использовать дословно):

```csharp
using System;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif

namespace AgentBridge
{
	internal static class FocusGuardNative
	{
#if UNITY_EDITOR_WIN
		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern bool SetForegroundWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

		public static long GetForegroundWindowHandle()
		{
			return GetForegroundWindow().ToInt64();
		}

		public static bool IsWindowAlive(long handle)
		{
			return handle != 0 && IsWindow(new IntPtr(handle));
		}

		public static bool TrySetForegroundWindow(long handle)
		{
			return SetForegroundWindow(new IntPtr(handle));
		}

		public static bool BelongsToCurrentProcess(long handle)
		{
			if (handle == 0)
			{
				return false;
			}

			uint pid;
			GetWindowThreadProcessId(new IntPtr(handle), out pid);
			return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
		}
#else
		public static long GetForegroundWindowHandle()
		{
			return 0;
		}

		public static bool IsWindowAlive(long handle)
		{
			return false;
		}

		public static bool TrySetForegroundWindow(long handle)
		{
			return false;
		}

		public static bool BelongsToCurrentProcess(long handle)
		{
			return false;
		}
#endif
	}
}
```

## Invariants (must hold throughout)

- Правки только в `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/**` и `AgentBridgeUnity/Assets/Tests/Editor/**`. Больше ни один файл не меняется (включая `package.json`, версии, CLI, SKILL.md).
- Сигнатуры существующих public методов пакета не меняются; статусы и формат журналов задач не меняются.
- Существующие тест-сьюты остаются зелёными.
- Код по CLAUDE.md: табы, без комментариев в новом коде, каждый тип в своём файле, никаких MonoBehaviour, никаких условий в одну строку.
- Никаких новых зависимостей и никаких изменений в ProjectSettings.

## Execution Plan

Units run in listed order unless a unit is marked [parallel].

### Unit 1 — FocusGuardNative

- Goal: файл `FocusGuardNative.cs` создан с кодом из Foundations дословно и проект компилируется.
- Touch: создать `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/FocusGuardNative.cs`.
- How: вставить код из Foundations без изменений.
- Gate: `agentbridge compile --format human` печатает `compile: success`.
- On failure: ≤2 попытки исправить ошибку компиляции, затем стоп и отчёт. Обходные пути не изобретать.

### Unit 2 — FocusGuard

- Goal: сторож фокуса работает через SessionState, переживает domain reload и восстанавливает `enterPlayModeBehavior`.
- Touch: создать `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/FocusGuard.cs`, public static class, `[InitializeOnLoad]`.
- How:
  - Статический конструктор: `EditorApplication.update += Tick;` и `EditorApplication.playModeStateChanged += OnPlayModeStateChanged;`.
  - `public static void BeginPlayEntryGuard()`: `Capture()`; `SetPlayUnfocused()`; записать `ActiveUntilUtc = DateTime.UtcNow.AddSeconds(120)`; `RestoreCount = 0`.
  - `public static void BeginWindowGuard()`: `Capture()`; записать `ActiveUntilUtc = DateTime.UtcNow.AddSeconds(5)`; `RestoreCount = 0`. Ключ `PrevPlayBehavior` не трогать.
  - `Capture()`: `long hwnd = FocusGuardNative.GetForegroundWindowHandle();` если `FocusGuardNative.BelongsToCurrentProcess(hwnd)` — сохранить `"0"`, иначе десятичную строку hwnd.
  - `SetPlayUnfocused()`: рефлексией `Type viewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");` если null — выйти. `PropertyInfo prop = viewType.GetProperty("enterPlayModeBehavior", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);` если null — выйти. `object unfocused;` через `Enum.Parse(prop.PropertyType, "PlayUnfocused")` в try/catch — при исключении выйти. Найти окна `Resources.FindObjectsOfTypeAll(viewType)`; если пусто — выйти. Сохранить в `PrevPlayBehavior` `Convert.ToInt32(prop.GetValue(первое окно))`, затем всем окнам выставить `unfocused`. Всё тело в try/catch, исключения глотать.
  - `OnPlayModeStateChanged(PlayModeStateChange state)`: при `EnteredPlayMode`, если ключ `ActiveUntilUtc` есть — переписать его на `DateTime.UtcNow.AddSeconds(5)`.
  - `Tick()`: если ключа `ActiveUntilUtc` нет — return. Если `DateTime.UtcNow` больше сохранённого — `Deactivate()`, return. Иначе: прочитать hwnd; если он не `"0"`, `FocusGuardNative.IsWindowAlive(hwnd)`, текущий foreground `BelongsToCurrentProcess`, и `RestoreCount < 2` — `FocusGuardNative.TrySetForegroundWindow(hwnd)` и инкрементировать `RestoreCount`.
  - `Deactivate()`: если ключ `PrevPlayBehavior` есть — тем же рефлексивным путём выставить сохранённое значение всем `PlayModeView` (try/catch, глотать). Стереть все четыре ключа через `SessionState.EraseString`.
- Gate: `agentbridge compile --format human` печатает `compile: success`.
- On failure: ≤2 попытки, затем стоп и отчёт.

### Unit 3 — вход в плеймод под стражей

- Goal: оба bridge-входа в Play Mode активируют сторож, а play-сессия не замирает в фоне.
- Touch: `AgentTestRunner.cs` — в `TryRequestRunForCoordinator` внутри существующего `try` непосредственно перед `api.Execute(new ExecutionSettings(filter));` добавить: `if (mode == TestMode.PlayMode) { FocusGuard.BeginPlayEntryGuard(); }`. `PlaySessionManager.cs` — в `BeginPlay` строкой перед `EditorApplication.EnterPlaymode();` добавить `FocusGuard.BeginPlayEntryGuard();`; в `ReconcileEntering` внутри ветки `if (EditorApplication.isPlaying)` первой строкой добавить `Application.runInBackground = true;` (нужен `using UnityEngine;` — уже есть).
- Gate: `agentbridge compile --format human` печатает `compile: success`; `grep -n "BeginPlayEntryGuard" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/AgentTestRunner.cs AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/PlaySessionManager.cs` находит по одному вхождению в каждом файле.
- On failure: ≤2 попытки, затем стоп и отчёт.

### Unit 4 — UnfocusedWindowShower

- Goal: helper показывает произвольное EditorWindow без активации и умеет увести его за экран.
- Touch: создать `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/UnfocusedWindowShower.cs`, internal static class.
- How:
  - `public static bool TryShow(EditorWindow window, Rect position, Action<string> warn)`:
    - `window.position = position;`
    - Рефлексией: `MethodInfo method = typeof(EditorWindow).GetMethod("ShowPopupWithMode", BindingFlags.Instance | BindingFlags.NonPublic);` и `Type showModeType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");`.
    - Если любой из них null — `warn("no-focus show is not available in this Unity version, falling back to a focused window");` затем `window.Show();` и вернуть false.
    - `object tooltipMode = Enum.Parse(showModeType, "Tooltip");` в try/catch; при исключении — тот же fallback с warn и false.
    - `method.Invoke(window, new object[] { tooltipMode, false });`
    - После показа: `window.position = new Rect(position.x, position.y, position.width, position.height);` (показ мог склампить rect через FitRectToScreen — вернуть запрошенный).
    - Вернуть true.
  - Никаких других публичных членов.
- Gate: `agentbridge compile --format human` печатает `compile: success`.
- On failure: ≤2 попытки, затем стоп и отчёт.

### Unit 5 — sceneshot без фокуса

- Goal: sceneshot больше не создаёт окно через `CreateWindow<SceneView>()` и не активирует Unity; картинки продолжают получаться.
- Touch: `SceneShot/SceneShotTaskExecutor.cs`, метод `TickPrepare`.
- How:
  - Заменить строку `_window = EditorWindow.CreateWindow<SceneView>();` на:
    - `_window = ScriptableObject.CreateInstance<SceneView>();`
  - Существующий блок, который выставляет `_window.position = new Rect(...)`, заменить на вызов `UnfocusedWindowShower.TryShow(_window, тот же Rect, message => _logs.Add(message));` — Rect считается так же, как сейчас (из workArea, Border, ppp, _targetPx).
  - Сразу после показа добавить `FocusGuard.BeginWindowGuard();`.
  - Порядок остальных действий не менять: titleContent, sceneLighting, drawGizmos, showGrid, HideOverlays, LookAt, Repaint, settle — как было. `CloseWindow()` не менять: `EditorWindow.Close()` корректно закрывает popup-контейнер.
- Gate: `agentbridge compile --format human` печатает `compile: success`; затем smoke: создать файл `AgentBridgeUnity/Temp/AgentBridge/Task_focusfix.sceneshot.json` с одним shot'ом `{"shots":[{"name":"focus_smoke","view":"scene","frame":{}}]}` (точный формат payload взять из SKILL.md unity-bridge), выполнить `agentbridge sceneshot AgentBridgeUnity/Temp/AgentBridge/Task_focusfix.sceneshot.json --wait 120 --format human` — статус `success` и в выводе путь к PNG-артефакту; файл существует и его размер больше 10000 байт (проверить `ls -la`).
- On failure: ≤3 попытки на smoke; если статус не success — прочитать Logs задачи из вывода CLI, исправить, повторить. Если после 3 попыток не success — стоп и отчёт. Запасные реализации (вернуть CreateWindow) не делать.

### Unit 6 — gameshot без фокуса

- Goal: gameshot не фокусирует Game View; если Game View нет — создаёт скрытый нефокусный.
- Touch: `SceneShot/SceneShotTaskExecutor.cs`, метод `PrepareGameShot`.
- How: заменить существующий try/catch с `EditorWindow.GetWindow(gameViewType, false, null, true)` на:
  - `Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");` если null — лог как сейчас и продолжить без окна.
  - `UnityEngine.Object[] views = Resources.FindObjectsOfTypeAll(gameViewType);`
  - Если `views.Length == 0`: `EditorWindow gameView = (EditorWindow)ScriptableObject.CreateInstance(gameViewType);` затем `UnfocusedWindowShower.TryShow(gameView, new Rect(workArea.x, workArea.y, 480f, 854f), message => _logs.Add(message));` где workArea = `EditorGUIUtility.GetMainWindowPosition()`. Существующие Game View не трогать вовсе (ни Focus, ни Repaint — в плей моде он перерисовывается сам).
  - После этого блока добавить `FocusGuard.BeginWindowGuard();`.
  - Остальное (CaptureScreenshot, ожидание файла, таймаут) не менять.
- Gate: `agentbridge compile --format human` печатает `compile: success`; `grep -n "GetWindow" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/SceneShot/SceneShotTaskExecutor.cs` ничего не находит.
- On failure: ≤2 попытки, затем стоп и отчёт.

### Unit 7 — тест нефокусного показа

- Goal: есть EditMode-тест, доказывающий, что helper показывает окно без фокуса.
- Touch: создать `AgentBridgeUnity/Assets/Tests/Editor/UnfocusedWindowTests.cs` в существующей тестовой asmdef-сборке (рядом лежат `PlayModeGuardrailTests.cs` и другие — asmdef уже есть, новый не создавать).
- How: один тест `TryShow_DoesNotFocusWindow`:
  - `SceneView view = ScriptableObject.CreateInstance<SceneView>();`
  - `bool shown = UnfocusedWindowShower.TryShow(view, new Rect(100f, 100f, 300f, 200f), _ => { });` (для доступа к internal классу добавить `"AgentBridge.Editor"`-сборку в `InternalsVisibleTo` нельзя — вместо этого проверить имя asmdef пакета и, если тестовая сборка уже ссылается на неё, использовать напрямую; если internal недоступен — сделать класс `UnfocusedWindowShower` public static вместо internal и не менять ничего больше).
  - `Assert.IsTrue(shown);`
  - `Assert.AreNotEqual(view, EditorWindow.focusedWindow);`
  - В finally: `view.Close();`
- Gate: `agentbridge tests --mode EditMode --format human` — весь сьют завершается со статусом `success`, `failed: 0`.
- On failure: ≤3 попытки; если fallback-ветка (`shown == false`) срабатывает на этой версии Unity — тест должен это зафиксировать провалом; в таком случае стоп и отчёт (значит рефлексия не нашла API и Unit 4 надо чинить, а не тест).

### Unit 8 — полная верификация

- Goal: вся система зелёная: компиляция, EditMode, PlayMode.
- Touch: ничего не менять; только запуск проверок.
- How и Gate (все три обязаны попасть в транскрипт):
  - `agentbridge compile --format human` → `compile: success`.
  - `agentbridge tests --mode EditMode --format human` → `failed: 0`.
  - `agentbridge tests --mode PlayMode --format human` → `failed: 0` (сьют `AgentBridgePlayModeProbeTests` существует; прогон заодно проверяет, что вход в плеймод со сторожем не сломан).
- On failure: упавший тест чинить правкой кода юнитов этого ТДД (не самих тестов, кроме случая, когда тест из Unit 7 written неверно); ≤3 попытки на каждый гейт, затем стоп и отчёт.

## Done (/goal condition)

Все следующие проверки выполнены и их вывод виден в транскрипте: `agentbridge compile --format human` печатает `compile: success`; `agentbridge tests --mode EditMode --format human` печатает `failed: 0`; `agentbridge tests --mode PlayMode --format human` печатает `failed: 0`; sceneshot-smoke из Unit 5 завершился статусом `success` и PNG-артефакт существует размером больше 10000 байт; `grep -rn "CreateWindow<SceneView>" AgentBridgeUnity/Packages` пусто; `grep -n "GetWindow" .../SceneShot/SceneShotTaskExecutor.cs` пусто; `grep -n "BeginPlayEntryGuard"` находит вызовы в AgentTestRunner.cs и PlaySessionManager.cs. Ограничения: изменены только файлы в `Packages/com.elmortem.agentbridge/Editor/**` и `Assets/Tests/Editor/**`; `package.json` не тронут. Стоп после 60 ходов.

## End-of-run report (the agent does this when the goal is met or it stops)

- Выставить Status в начале файла в `Выполнено`.
- Отчитаться: какие юниты закрыты, какие гейты потребовали повторов, на чём остановился и почему (если остановился).
- Флаг — не действовать самому: уточни у заказчика, нужно ли обновлять UNITYAGENT.md / SKILL.md unity-bridge под новое поведение (окна больше не фокусируются, play-сессия включает runInBackground).

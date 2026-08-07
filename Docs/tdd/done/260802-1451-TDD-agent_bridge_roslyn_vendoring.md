Status: Выполнено

# Unity Agent Bridge — Вендоринг Roslyn в пакет — Agent Execution Spec

## References (not inlined)

- Конвенции кода: глобальный CLAUDE.md пользователя (табы, один тип на файл, без комментариев в коде, сериализуемые поля public с большой буквы).
- Движок: `Docs/tdd/done/260725-1855-TDD-agent_bridge_roslyn_engine.md` — контракт протокола и статусов остаётся источником истины. Этот документ отменяет только Unit 7 и Unit 8 того ТДД (четыре источника Roslyn с догрузкой из NuGet).
- Предыдущие правки: `Docs/tdd/260726-1227-TDD-agent_bridge_review_fixes.md`.
- Skills: `unity-bridge`, `unity-ui` — протокол клиента описан там.
- Эталон реализации: MCP for Unity (CoplayDev), `MCPForUnity/Editor/Setup/RoslynInstaller.cs` и `MCPForUnity/Editor/Tools/ExecuteCode.cs`. Живой агент-мост, грузящий Roslyn в домен редактора через рефлексию и компилирующий в память — та же схема, что у нас. Из него взят проверенный набор сборок и версий.

## Контекст (что сломано и почему)

- Источник `UnityBuiltin` не может работать ни в одной версии Unity. `Editor/Data/DotNetSdkRoslyn/*.dll` — сборки CoreCLR, Mono редактора их не грузит: `Could not load image ... due to Invalid data directory 3` → `BadImageFormatException`. Воспроизведено на `6000.4.0f1` (скриншот окна) и на `2022.3.62f2` (`AgentBridgeUnity/Logs/AssetImportWorker1-prev.log:6548`).
- Спам ошибок в консоли: `AgentBridgeSetupWindow.DrawRow` зовёт `RoslynResolver.Probe` для всех источников на каждый `OnGUI`, а проба выполняет реальный `Assembly.LoadFrom`. Кэша отрицательного результата нет.
- Тот же спам в Asset Import Worker: `BridgeStatusWriter` помечен `[InitializeOnLoad]` и в статическом конструкторе зовёт `RoslynResolver.ResolveConfigured()`, но, в отличие от `AgentBridgeSetupBootstrap`, не имеет проверки на batch mode. Оттуда же берётся `Sharing violation on path ...status.json.tmp` в тех же логах.
- Кнопка `Download` физически недостижима: окно жёстко 460×320 (`maxSize == minSize`), а строка — `110 + гибкое описание + 140 причина + 80 кнопка`; длинный текст причины выдавливает кнопку за правый край. `OnInstallCompleted` игнорирует `success`/`message`, поэтому упавшая закачка выглядит как «ничего не произошло».
- Набор пакетов в `RoslynInstaller` (10 штук, `microsoft.codeanalysis.* 4.9.2`) никогда не проверялся: `Library/AgentBridge/Roslyn/` пуст, ни одна DLL не скачивалась.
- `RoslynResolver.OnAssemblyResolve` возвращает `null`, если сборка с таким коротким именем уже загружена. Для `System.Runtime.CompilerServices.Unsafe` это фатально: Unity держит v4.x, Roslyn для netstandard2.0 ссылается ровно на v6.0.0.0, и статический конструктор `StringTable` падает с `FileNotFoundException`. Этот дефект проявился бы сразу же, как только закачка заработала.
- `RoslynResolver.TryLoadAndVerify` пишет `BridgeStatusWriter.Current.RoslynSource` из любой удачной пробы. После загрузки вендоренных сборок проба `Project` находит их среди загруженных и перезаписывает статус на `Project`.

## Решение (закрытые вопросы, альтернативы отклонены)

- Roslyn вендорится в пакет. Догрузка из сети удаляется целиком вместе с `RoslynInstaller.cs`. Отклонено: чинить установщик — мост агентский, «поставил пакет и работает» важнее экономии 8 МБ в репозитории.
- Сборки лежат в `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Roslyn~/`. Суффикс `~` выводит папку из-под импортёра Unity: нет `.meta`, нет `PluginImporter`, нет попадания в билды, нет правок asmdef. UPM переносит такие папки и при установке по git URL, и в тарболе. Отклонено: `Assets/Plugins/Roslyn` (как в MCP for Unity) — тянет за собой импорт-сеттинги, попадание в билд и конфликты с чужими плагинами проекта.
- Набор сборок — ровно пять, все из `lib/netstandard2.0/`, версии из проверенного набора MCP for Unity:
  - `microsoft.codeanalysis.common` `4.12.0` → `Microsoft.CodeAnalysis.dll`
  - `microsoft.codeanalysis.csharp` `4.12.0` → `Microsoft.CodeAnalysis.CSharp.dll`
  - `system.collections.immutable` `8.0.0` → `System.Collections.Immutable.dll`
  - `system.reflection.metadata` `8.0.0` → `System.Reflection.Metadata.dll`
  - `system.runtime.compilerservices.unsafe` `6.0.0` → `System.Runtime.CompilerServices.Unsafe.dll`
  Отклонено: текущие 10 пакетов. Фасадные сборки `System.Memory`, `System.Buffers`, `System.Numerics.Vectors`, `System.Threading.Tasks.Extensions`, `System.Text.Encoding.CodePages` числятся в зависимостях nuspec, но в эталоне не поставляются: Mono редактора закрывает их сам. Они добавляются только по лестнице отказа в Unit 2, а не превентивно.
- `RoslynSourceKind` становится `Auto`, `Vendored`, `Project`, `Local`. `UnityBuiltin` и `NuGet` удаляются. Порядок `Auto`: `Project`, `Local`, `Vendored` — если Roslyn уже загружен проектом, вторая копия `Microsoft.CodeAnalysis` в домене хуже, чем чужая версия.
- `SourceGuardrail`, `RoslynCompiler`, `ReferenceCatalog`, формат журнала и протокол не меняются: Roslyn теперь гарантированно доступен, syntax tree на месте.
- Все C#-правки перевода на `Vendored` сделаны одним юнитом. Причина: любое промежуточное состояние (например, enum заменён, а окно ещё ссылается на `UnityBuiltin`) не компилируется, мост умирает, и агент теряет единственный канал проверки.

## Предусловия (проверить первым делом, при невыполнении — остановиться и сообщить)

- Корень репозитория: `D:\Hobby\Repositories\unitycoworkbridge`, ветка `roslyn-cli`.
- Первым действием выполнить `git status --porcelain -uall` и зафиксировать вывод в транскрипте как базовую линию. В репозитории уже есть чужие незакоммиченные правки. Ничего из базовой линии не откатывать, не коммитить и не трогать. Все проверки раздела Done сравниваются с этой базовой линией, а не с чистым деревом.
- Создать рабочий каталог вне репозитория: `mkdir -p /d/Temp/agentbridge`. Дальше он обозначается `<WORK>` = `D:/Temp/agentbridge`. Всё временное (скачанные `.nupkg`, пробные `.cs`) кладётся только туда.
- Unity Editor запущен с проектом `AgentBridgeUnity`: файл `AgentBridgeUnity/Library/EditorInstance.json` существует.
- `agentbridge status` печатает JSON с `"BridgeReady": true`. Если команда не найдена — остановиться и сообщить: «Поставь CLI по инструкции из README (`scripts/install-agentbridge.ps1`) и запусти меня заново». Не ставить CLI самостоятельно. Если `BridgeReady` ложно — остановиться и сообщить: «Открой AgentBridgeUnity в Unity и включи мост через Tools → Agent Bridge → Start». Не запускать Unity самостоятельно.
- `pwsh --version` печатает версию PowerShell 7 или новее.
- Есть доступ к `api.nuget.org` (нужен один раз, в Unit 1).

## Foundations (shared, used across units)

### Оболочки

- Все гейты и проверки (`grep`, `test`, `ls`, `cat`, `git`, `agentbridge`) выполняются в bash из корня репозитория.
- В PowerShell выполняется только `pwsh -File scripts/fetch-roslyn.ps1`.
- Пути в bash на этой машине: корень репозитория — текущий каталог, `<WORK>` — `/d/Temp/agentbridge`.

### Пути

- Пакет: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/`, дальше в тексте `<PKG>`.
- Каталог вендоринга: `<PKG>/Roslyn~/`. Внутри: пять `.dll`, `roslyn.lock.json`, `THIRD-PARTY-NOTICES.md`.

### Формат `roslyn.lock.json`

Записи в том же порядке, что в списке решения выше:

```json
{
  "packages": [
    {
      "id": "microsoft.codeanalysis.common",
      "version": "4.12.0",
      "entry": "lib/netstandard2.0/Microsoft.CodeAnalysis.dll",
      "file": "Microsoft.CodeAnalysis.dll",
      "sha256": "<64 hex-символа в нижнем регистре>"
    }
  ]
}
```

### Итоговый enum

Файл `<PKG>/Editor/RoslynSourceKind.cs` целиком:

```csharp
namespace AgentBridge
{
	public enum RoslynSourceKind
	{
		Auto,
		Vendored,
		Project,
		Local
	}
}
```

### Новый публичный API `RoslynResolver`

Сигнатуры фиксированы, менять нельзя:

```csharp
public static RoslynLocation Probe(RoslynSourceKind kind);
public static RoslynLocation ProbeCached(RoslynSourceKind kind);
public static void ClearProbeCache();
```

### Шаблоны пробных задач

Имя класса обязано совпадать с именем файла без расширения, иначе мост вернёт `rejected` до компиляции. Файл всегда создаётся в `<WORK>`, запускается как `agentbridge csharp /d/Temp/agentbridge/<Id>.cs`.

Smoke-задача, `<WORK>/Task_smoke.cs`:

```csharp
using System.Threading.Tasks;

public static class Task_smoke
{
	public static async Task<string> Run()
	{
		await Task.Delay(100);
		return "ok";
	}
}
```

Проба источников, `<WORK>/Task_probe.cs`:

```csharp
using System.Threading.Tasks;

public static class Task_probe
{
	public static async Task<string> Run()
	{
		await Task.Delay(1);
		return AgentBridge.RoslynProbe.Run();
	}
}
```

Ошибка компиляции, `<WORK>/Task_cserr.cs`:

```csharp
using System.Threading.Tasks;

public static class Task_cserr
{
	public static async Task<string> Run()
	{
		await Task.Delay(1);
		return undefinedVariable;
	}
}
```

Нарушение guardrail, `<WORK>/Task_guard.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

public static class Task_guard
{
	public static async Task<string> Run()
	{
		Thread.Sleep(100);
		await Task.Delay(1);
		return "ok";
	}
}
```

Проверка окна, `<WORK>/Task_window.cs`:

```csharp
using System.Threading.Tasks;
using UnityEditor;

public static class Task_window
{
	public static async Task<string> Run()
	{
		AgentBridge.AgentBridgeSetupWindow window = EditorWindow.GetWindow<AgentBridge.AgentBridgeSetupWindow>(true);
		window.Focus();
		await Task.Delay(500);
		window.Close();
		return "ok";
	}
}
```

### Правила выхода из тупика (действуют во всех юнитах)

- После каждой правки C# в пакете выполнять `agentbridge compile` и добиваться `"Status": "success"` — это и деплой (domain reload подхватывает правку), и smoke-проверка.
- Если `agentbridge compile` вернул `"Status": "compiler_error"` — читать `Diagnostics[]` (`File`, `Line`, `Message`) и чинить именно эти места, даже если файл не перечислен в `Touch` текущего юнита. Это не нарушение инвариантов.
- Если CLI не вернул JSON за отведённое ожидание — выполнить `agentbridge status`. При `"BridgeReady": false` остановиться и сообщить: «мост не отвечает, открой Unity и посмотри консоль». Unity самостоятельно не перезапускать, домен принудительно не перезагружать никакими способами, кроме `agentbridge compile`.
- Результаты пробы Roslyn, полученные до успешного `agentbridge compile`, недействительны: домен держит старую сборку пакета. Никаких выводов о работоспособности Roslyn по ним не делать.

## Invariants (must hold throughout)

- Изменяются только файлы внутри корня репозитория. Исключение: временные файлы задач и скачанные `.nupkg` создаются в `<WORK>` и инвариантом не покрываются.
- Файлы и правки из базовой линии `git status --porcelain -uall`, снятой в предусловиях, не откатываются и не изменяются.
- `Docs/tdd/done/**` не изменяется.
- Формат JSON-конверта задач и записей журнала не меняется: ни одно поле не добавляется, не удаляется и не переименовывается.
- Файлы `Editor/Ui/UiNodeApplier.cs`, `Editor/Ui/UiDumper.cs`, `Editor/Ui/UiScreenshot.cs`, `Editor/Ui/UiComponentSync.cs` не изменяются.
- Файлы `Editor/RoslynCompiler.cs`, `Editor/SourceGuardrail.cs`, `Editor/RoslynReflectionHelper.cs`, `Editor/ReferenceCatalog.cs` не изменяются.
- Под `AgentBridgeUnity/Assets/` не появляется ни одного нового файла.
- `AgentBridgeUnity/Packages/manifest.json` и блок `dependencies` в `<PKG>/package.json` не меняются.
- В коде нет комментариев, отступы — табы, каждый тип в своём файле.
- Ни одна вендоренная DLL не попадает под `.gitignore`: `git check-ignore -v <путь>` для каждой возвращает код 1.

## Execution Plan

Юниты выполняются строго по порядку.

### Unit 1 — Скрипт вендоринга и загрузка набора DLL

- Goal: в `<PKG>/Roslyn~/` лежат пять DLL нужных версий, лок-файл с sha256 и файл лицензий; всё это видно git.
- Touch: новый `scripts/fetch-roslyn.ps1`; новые файлы в `<PKG>/Roslyn~/`.
- How: скрипт на PowerShell 7 без параметров. Внутри — таблица из пяти записей `id`/`version`/`entry`/`file` ровно из раздела решения. Логика: создать временный каталог `Join-Path $env:TEMP "roslyn-fetch"`; для каждой записи скачать `https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.<version>.nupkg` через `Invoke-WebRequest -UseBasicParsing -OutFile` в этот временный каталог; открыть через `[System.IO.Compression.ZipFile]::OpenRead($nupkgPath)`; найти запись, чей `FullName` после замены `\` на `/` равен `entry` без учёта регистра; распаковать в `<PKG>/Roslyn~/<file>`; посчитать `(Get-FileHash -Algorithm SHA256 $dest).Hash.ToLowerInvariant()`. Если запись `entry` в архиве не найдена — писать в stderr имя пакета и завершаться кодом 1. Временный каталог удалять в `finally`. В `<PKG>/Roslyn~/` не должно попасть ни одного `.nupkg`. После всех пяти — записать `<PKG>/Roslyn~/roslyn.lock.json` в формате из Foundations и `<PKG>/Roslyn~/THIRD-PARTY-NOTICES.md` с полным текстом лицензии MIT и перечислением пяти пакетов с версиями и ссылками вида `https://www.nuget.org/packages/<Id>/<version>`. Скрипт идемпотентен: перезаписывает файлы. Затем выполнить `pwsh -File scripts/fetch-roslyn.ps1`.
- Gate: `ls -1 AgentBridgeUnity/Packages/com.elmortem.agentbridge/Roslyn~/ | wc -l` печатает `7`; `ls AgentBridgeUnity/Packages/com.elmortem.agentbridge/Roslyn~/*.nupkg` завершается кодом 2 (файлов нет); `grep -o '"sha256": "[0-9a-f]\{64\}"' AgentBridgeUnity/Packages/com.elmortem.agentbridge/Roslyn~/roslyn.lock.json | wc -l` печатает `5`; `git status --porcelain -uall AgentBridgeUnity/Packages/com.elmortem.agentbridge/Roslyn~/` печатает ровно 7 строк; `git check-ignore -v` для каждой из пяти DLL завершается кодом 1.
- On failure: если nuget.org недоступен или отдаёт не 200 — ≤3 попытки, затем остановиться и сообщить, какой пакет не скачался. Не подменять версии, не брать DLL из другого источника, не искать их на диске.

### Unit 2 — Перевод пакета на источник Vendored

- Goal: мост находит вендоренный Roslyn, грузит его один раз за домен, корректно отдаёт `System.Runtime.CompilerServices.Unsafe` v6 поверх юнитивской v4, а окно установки больше не грузит сборки на каждый кадр и не обрезает содержимое.
- Touch: `<PKG>/Editor/RoslynSourceKind.cs`, `<PKG>/Editor/RoslynResolver.cs`, `<PKG>/Editor/RoslynProbe.cs`, `<PKG>/Editor/BridgePaths.cs`, `<PKG>/Editor/AgentBridgeSetupWindow.cs`, `<PKG>/Editor/AgentBridgeSettingsStore.cs`; удалить `<PKG>/Editor/RoslynInstaller.cs` и `<PKG>/Editor/RoslynInstaller.cs.meta`. Все правки делаются до первого запуска `agentbridge compile`: промежуточные состояния не компилируются.
- How: семь правок.
  - `RoslynSourceKind.cs` заменить целиком на текст из Foundations.
  - Из `BridgePaths.cs` удалить свойство `Roslyn` целиком. Удалить файлы `RoslynInstaller.cs` и `RoslynInstaller.cs.meta`.
  - В `RoslynResolver.cs` добавить `using System.Collections.Generic;` (сейчас его там нет; `using System;`, `System.IO`, `System.Reflection`, `UnityEditor`, `UnityEngine` уже есть и остаются). Добавить поле и методы:

```csharp
private static readonly Dictionary<RoslynSourceKind, RoslynLocation> _probeCache = new Dictionary<RoslynSourceKind, RoslynLocation>();

public static RoslynLocation ProbeCached(RoslynSourceKind kind)
{
	RoslynLocation cached;
	if (_probeCache.TryGetValue(kind, out cached))
	{
		return cached;
	}

	RoslynLocation location = Probe(kind);
	_probeCache[kind] = location;
	return location;
}

public static void ClearProbeCache()
{
	_probeCache.Clear();
}

private static string GetVendoredDirectory()
{
	UnityEditor.PackageManager.PackageInfo package =
		UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RoslynResolver).Assembly);
	if (package == null || string.IsNullOrEmpty(package.resolvedPath))
	{
		return null;
	}

	return Path.Combine(package.resolvedPath, "Roslyn~");
}

private static RoslynLocation ProbeVendored()
{
	string directory = GetVendoredDirectory();
	if (directory == null)
	{
		return new RoslynLocation { Kind = RoslynSourceKind.Vendored, Available = false, Reason = "package path unavailable" };
	}

	return ProbeDirectory(RoslynSourceKind.Vendored, directory);
}
```

  - В `RoslynResolver.Probe` ветку `UnityBuiltin` заменить на `case RoslynSourceKind.Vendored: return ProbeVendored();`, ветку `NuGet` удалить. Приватный метод `ProbeDirectorySearch` после этого не используется — удалить его целиком. `FindFileRecursive`, `SearchDirectory` и константа `MaxSearchDepth` остаются: их использует `ProbeProject`. `ResolveAuto` перечисляет `Project`, `Local`, `Vendored` и зовёт `ProbeCached`. `ResolveConfigured` зовёт `ProbeCached`.
  - В `RoslynResolver.ProbeProject` первый цикл по загруженным сборкам не должен принимать вендоренную копию за проектную. Заменить тело цикла на:

```csharp
string vendoredDirectory = GetVendoredDirectory();

foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
{
	if (assembly.GetName().Name != "Microsoft.CodeAnalysis.CSharp")
	{
		continue;
	}

	string assemblyLocation;
	try
	{
		assemblyLocation = assembly.Location;
	}
	catch
	{
		continue;
	}

	if (vendoredDirectory != null && !string.IsNullOrEmpty(assemblyLocation)
		&& Path.GetFullPath(assemblyLocation).StartsWith(Path.GetFullPath(vendoredDirectory), StringComparison.OrdinalIgnoreCase))
	{
		continue;
	}

	return TryLoadAndVerify(RoslynSourceKind.Project, assemblyLocation);
}
```

  - В `RoslynResolver.TryLoadAndVerify` удалить три строки, пишущие статус (`BridgeStatusWriter.Current.RoslynReady = true;`, `BridgeStatusWriter.Current.RoslynSource = kind.ToString();`, `BridgeStatusWriter.Write();`). Статус остаётся за `BridgeStatusWriter.WriteOnLoad`, который и так выставляет оба поля по результату `ResolveConfigured`. Проба перестаёт иметь побочные эффекты.
  - `OnAssemblyResolve` заменить целиком на:

```csharp
private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
{
	if (string.IsNullOrEmpty(_activeDirectory) || !Directory.Exists(_activeDirectory))
	{
		return null;
	}

	var requested = new AssemblyName(args.Name);
	string candidate = Path.Combine(_activeDirectory, requested.Name + ".dll");

	if (File.Exists(candidate))
	{
		try
		{
			AssemblyName candidateName = AssemblyName.GetAssemblyName(candidate);
			if (requested.Version == null || candidateName.Version >= requested.Version)
			{
				return Assembly.LoadFrom(candidate);
			}
		}
		catch
		{
		}
	}

	foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
	{
		if (loaded.GetName().Name != requested.Name)
		{
			continue;
		}

		if (requested.Version == null || loaded.GetName().Version >= requested.Version)
		{
			return loaded;
		}
	}

	return null;
}
```

  - `RoslynProbe.Run` перечисляет `Vendored`, `Project`, `Local` и зовёт `ProbeCached` вместо `Probe`.
  - `AgentBridgeSetupWindow.cs`: массив `Sources` — `Vendored`, `Project`, `Local`. В `Open()` оставить только `window.minSize = new Vector2(560, 260);`, строку с `maxSize` удалить. Добавить поле `private Vector2 _scroll;`. Методы `OnEnable`, `OnDisable` и `OnInstallCompleted` удалить целиком вместе с подпиской на `RoslynInstaller.Completed`. `OnGUI`: заголовок `Roslyn source` через `EditorGUILayout.LabelField(..., EditorStyles.boldLabel)`, `EditorGUILayout.Space()`, затем `_scroll = EditorGUILayout.BeginScrollView(_scroll);`, цикл `DrawRow` по `Sources`, `EditorGUILayout.EndScrollView();`, затем горизонталь из двух кнопок: `Refresh` вызывает `RoslynResolver.ClearProbeCache()` и `Repaint()`; `Close` ставит `SessionState.SetBool("AgentBridge_SetupDismissed", true)` и вызывает `Close()`. Обёртки `EditorGUI.DisabledScope(RoslynInstaller.IsBusy)` убрать. `DrawRow` переписать: `RoslynLocation location = RoslynResolver.ProbeCached(kind);`, затем `EditorGUILayout.LabelField(kind + " — " + DescriptionFor(kind), EditorStyles.boldLabel);`, затем горизонталь из `EditorGUILayout.SelectableLabel(location.Available ? "Ready" : location.Reason, GUILayout.Height(EditorGUIUtility.singleLineHeight))` и кнопки `Use` шириной 60 внутри `EditorGUI.DisabledScope(!location.Available)`, пишущей `AgentBridgeSettingsStore.SetRoslynSource(kind.ToString())`, затем `EditorGUILayout.Space()`. `DescriptionFor`: `Vendored` → `"Roslyn shipped with the package"`, `Project` → `"Roslyn already referenced by the project"`, `Local` → `"Roslyn from a local folder"`.
  - `AgentBridgeSettingsStore.cs`: в существующий метод `Initialize()` (он помечен `[InitializeOnLoadMethod]`) после присвоения `_mainThreadId` добавить вызов нового приватного метода `MigrateRoslynSource()`. Метод: `AgentBridgeSettings settings = Load();` если `settings.RoslynSource` равен `"UnityBuiltin"` или `"NuGet"` (сравнение `StringComparison.Ordinal`) — присвоить `"Auto"` и вызвать `Save(settings)`; иначе ничего не делать. В `Load()` миграцию не добавлять: `Load()` зовётся из worker thread во время компиляции.
- Gate: шесть проверок подряд.
  - `test ! -f AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/RoslynInstaller.cs && test ! -f AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/RoslynInstaller.cs.meta` завершается кодом 0.
  - `grep -rn "UnityBuiltin\|RoslynSourceKind.NuGet\|RoslynInstaller\|BridgePaths.Roslyn\|maxSize\|ProbeDirectorySearch" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/` не печатает ничего.
  - `grep -rc "ProbeCached" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/AgentBridgeSetupWindow.cs AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/RoslynProbe.cs` печатает для обоих файлов значение не меньше 1.
  - `agentbridge compile` → `"Status": "success"`.
  - Задача `Task_probe` из Foundations → `"Status": "success"`, `ReturnValue` начинается с `Vendored=ok`.
  - Миграция настроек: дописать в `AgentBridgeUnity/ProjectSettings/AgentBridge.json` поле `"RoslynSource":"NuGet"`, подождать 3 секунды, выполнить `agentbridge compile` → `"Status": "success"`, затем `grep -o '"RoslynSource": *"[A-Za-z]*"' AgentBridgeUnity/ProjectSettings/AgentBridge.json` печатает `Auto`.
- On failure: разделять два случая.
  - `agentbridge compile` даёт `compiler_error` — чинить по `Diagnostics[]`, ≤5 попыток, затем остановиться и сообщить последние диагностики дословно.
  - `compile` успешен, но `Task_probe` даёт `Vendored=` с текстом ошибки загрузки — это конфликт версий сборок. Лестница из двух шагов, дальше не идти. Каждый шаг: правка таблицы в `scripts/fetch-roslyn.ps1`, `pwsh -File scripts/fetch-roslyn.ps1`, затем обязательно `agentbridge compile` до `"Status": "success"` (только он перезагружает домен и сбрасывает `_probeCache`), и только после этого повторный `Task_probe`.
    - шаг 1: дописать в таблицу пять фасадных пакетов, все из `lib/netstandard2.0/`: `system.memory 4.5.5`, `system.buffers 4.5.1`, `system.numerics.vectors 4.5.0`, `system.threading.tasks.extensions 4.5.4`, `system.text.encoding.codepages 7.0.0`. Ожидаемое число файлов в `Roslyn~/` становится 12, гейт Unit 1 пересчитать соответственно.
    - шаг 2: дополнительно поднять `microsoft.codeanalysis.common` и `microsoft.codeanalysis.csharp` до `4.14.0`.
    Версии `system.collections.immutable 8.0.0`, `system.reflection.metadata 8.0.0` и `system.runtime.compilerservices.unsafe 6.0.0` не менять: они совпадают с эталоном. Если и после второго шага проба не `ok` — остановиться и сообщить дословный `Reason` и полный `ReturnValue` пробы. Не менять способ загрузки, не переносить DLL в `Assets/Plugins`.

### Unit 3 — Окно установки не падает

- Goal: окно открывается и отрисовывается без исключений после переписывания.
- Touch: ничего не править, только проверять.
- How: выполнить задачу `Task_window` из Foundations.
- Gate: `"Status": "success"`, и в массиве `Logs` результата нет строки, содержащей `Exception`.
- On failure: ≤3 попытки; если в `Logs` есть исключение — починить `AgentBridgeSetupWindow.cs` по тексту стектрейса и повторить, всего не более 3 циклов, затем остановиться и сообщить. Окно в любом исходе оставить закрытым.

### Unit 4 — Статус-райтер не работает в Asset Import Worker

- Goal: импорт-воркеры не резолвят Roslyn, не пишут `status.json` и не дерутся за `status.json.tmp`.
- Touch: `<PKG>/Editor/BridgeStatusWriter.cs`.
- How: в статический конструктор `BridgeStatusWriter()` первой строкой добавить `if (Application.isBatchMode) { return; }` — до `WriteOnLoad()` и до подписки на `EditorApplication.update`.
- Gate: `grep -n "isBatchMode" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/BridgeStatusWriter.cs` печатает 1 строку; `agentbridge compile` → `"Status": "success"`; `agentbridge status` печатает JSON с непустым `SessionId`.
- On failure: одна попытка, затем остановиться и сообщить.

### Unit 5 — Приёмка на живом редакторе

- Goal: мост компилирует и исполняет задачи на вендоренном Roslyn, диагностика и guardrail работают как раньше.
- Touch: ничего не править, только проверять.
- How: выполнить четыре проверки подряд и привести их вывод целиком.
  - `agentbridge doctor` (формат по умолчанию — JSON; `--format human` не использовать, он не печатает источник) — в выводе `"RoslynReady": true` и `"RoslynSource": "Vendored"`.
  - `Task_smoke` → `"Status": "success"`, `ReturnValue` равен `ok`.
  - `Task_cserr` → `"Status": "compiler_error"`, в `Diagnostics` есть запись с `"Code": "CS0103"` и ненулевыми `Line` и `Column`.
  - `Task_guard` → `"Status": "rejected"`, в `Logs` есть строка, содержащая `guardrail`.
- Gate: все четыре проверки дали описанный результат, вывод каждой присутствует в транскрипте.
- On failure: ≤3 попытки на проверку. Если падает третья или четвёртая — это регрессия `RoslynCompiler`/`SourceGuardrail`, которые править запрещено инвариантами: остановиться и сообщить.

### Unit 6 — Документация

- Goal: README не описывает удалённый механизм.
- Touch: `README.md`.
- How: три точечные правки, ничего сверх них.
  - В блоке `## Working Directory` удалить строку `├── Roslyn/                     ← downloaded Roslyn assemblies (NuGet source only)` целиком.
  - В разделе `## Limitations` последний буллет, начинающийся с `Roslyn is not bundled with the package`, заменить целиком на: `- Roslyn ships inside the package (`Roslyn~/`), so no download and no network access are required; third-party licenses are in `Roslyn~/THIRD-PARTY-NOTICES.md`.`
  - В раздел про установку пакета, сразу после строки `The package has no dependencies on other project assemblies and will work even if the project has compilation errors.`, добавить абзац: `Roslyn is bundled in the package under `Roslyn~/` — nothing to download and no setup step.`
  Файл `unity-bridge-plugin/skills/unity-bridge/SKILL.md` не менять: упоминаний источников Roslyn и окна установки в нём нет, только факт компиляции через Roslyn, который остаётся верным.
- Gate: `grep -c "downloads it from NuGet\|NuGet source only\|Roslyn is not bundled" README.md` печатает `0`; `grep -c "Roslyn~" README.md` печатает значение не меньше `2`; `git status --porcelain -uall unity-bridge-plugin/` печатает то же, что в базовой линии.
- On failure: одна попытка, затем остановиться и сообщить.

## Done (/goal condition)

Все шесть юнитов закрыты своими гейтами, и это подтверждено в транскрипте следующим: `ls -1 AgentBridgeUnity/Packages/com.elmortem.agentbridge/Roslyn~/ | wc -l` печатает число файлов, совпадающее с итоговой таблицей `scripts/fetch-roslyn.ps1` (7 при базовом наборе, 12 после шага 1 лестницы), и среди них есть `Microsoft.CodeAnalysis.CSharp.dll` и `roslyn.lock.json`; `grep -rn "UnityBuiltin\|RoslynInstaller\|BridgePaths.Roslyn" AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/` не печатает ничего; `agentbridge compile` печатает `"Status": "success"`; `agentbridge doctor` печатает `"RoslynReady": true` и `"RoslynSource": "Vendored"`; задача `Task_probe` печатает `ReturnValue`, начинающийся с `Vendored=ok`; `Task_smoke` печатает `"Status": "success"`; `Task_window` печатает `"Status": "success"` без `Exception` в `Logs`; `Task_cserr` печатает `"Status": "compiler_error"` и `CS0103`; `Task_guard` печатает `"Status": "rejected"`. При этом `git status --porcelain -uall` отличается от базовой линии, снятой в предусловиях, только файлами внутри `AgentBridgeUnity/Packages/com.elmortem.agentbridge/`, `scripts/fetch-roslyn.ps1`, `README.md` и `Docs/tdd/260802-1451-TDD-agent_bridge_roslyn_vendoring.md`, и ни одной новой строки под `AgentBridgeUnity/Assets/`. Остановиться после 100 ходов.

## End-of-run report (the agent does this when the goal is met or it stops)

- Перед любой остановкой, успешной или нет, убедиться, что `agentbridge compile` даёт `"Status": "success"`. Если нет — довести правки до компилирующегося состояния и только затем останавливаться: иначе у пользователя остаётся мёртвый мост.
- Установить `Status` в начале документа в `Выполнено`.
- Сообщить: какие юниты закрыты; какие гейты потребовали повторов и почему; понадобилась ли лестница версий из Unit 2 и на каком шаге остановилась; итоговый состав `Roslyn~/`; на чём остановился, если остановился.
- Flag — не действовать: уточни у заказчика, нужно ли обновлять проектную документацию под эти изменения.

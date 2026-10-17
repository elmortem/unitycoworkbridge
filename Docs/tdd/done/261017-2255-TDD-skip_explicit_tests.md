Status: Выполнено

# Обычный прогон тестов пропускает `[Explicit]`, `--category` понимает исключения

Сейчас `agentbridge tests --assembly X` запускает все `[Explicit]`-тесты сборки. Unity 1.1.33 строит из `assemblyNames` фильтр `AssemblyNameFilter`, а он напрямую совпадает с фикстурой и методом по `TypeInfo`, поэтому `IsExplicitMatch` даёт `true`. После правки прогон без `--test` и без включающей `--category` не запускает explicit-тесты: Unity показывает их как `Skipped:Explicit`. Запуск по имени и по включающей категории работает как раньше. Кроме того, `--category '!Perf'` исключает категорию: Unity это уже умеет (`RuntimeTestRunnerFilter.AddFilters` превращает `!X` в `NotFilter`), а мост научится так же разрешать выборку и кэш. Прогон, где заданы только исключающие категории, CLI считает прогоном всего набора и требует `--confirm-all`.

Факты, проверенные живым прогоном в `AgentBridgeUnity`: `AndFilter`, в который входит `NotFilter`, даёт `IsExplicitMatch = false` для любого узла, в том числе для явно названного по имени теста. NUnit-строка explicit-пропуска — `ResultState.Explicit.ToString() == "Skipped:Explicit"`. `TestResultAdaptor.ResultState` хранит именно её.

## Правило выборки explicit

Запрос «выбирает explicit явно», если в нём есть хотя бы одно имя `--test` или хотя бы одна категория без префикса `!`. Если запрос explicit явно не выбирает, мост добавляет в фильтр Unity служебную категорию-исключение. Она гасит explicit-совпадение и ничего не отсекает, потому что такой категории ни у кого нет. Каталог и кэш следуют тому же правилу.

## Пакет `AgentBridge` (`AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/`)

### `TestNameResolver.cs`

- `Node`: новое поле `public bool Explicit;`. Оно равно `true`, если `RunState.Explicit` стоит у самого узла или у любого предка. Используется `UnityEditor.TestTools.TestRunner.Api.RunState`, namespace уже подключён.
- `Collect` получает параметр `bool underExplicit`. В `Catalog` передаётся `false`.

```csharp
bool isExplicit = underExplicit || test.RunState == RunState.Explicit;
nodes.Add(new Node { Name = test.Name, FullName = test.FullName, Assembly = assembly,
	IsSuite = test.IsSuite, Explicit = isExplicit, Ancestors = new List<string>(ancestors), Categories = inherited });
...
foreach (ITestAdaptor child in test.Children) Collect(child, assembly, parents, inherited, isExplicit, nodes);
```

- Новый публичный метод:

```csharp
public static bool SelectsExplicitly(string[] names, string[] categories)
{
	if (names != null && names.Length > 0)
	{
		return true;
	}
	if (categories == null)
	{
		return false;
	}
	foreach (string category in categories)
	{
		if (!IsExclusion(category))
		{
			return true;
		}
	}
	return false;
}

public static bool IsExclusion(string category)
{
	return category != null && category.StartsWith("!", StringComparison.Ordinal);
}
```

- В `SelectCases` к условию добавляется `&& (!node.Explicit || SelectsExplicitly(resolved, categories))`. `resolved` пуст ровно тогда, когда пуст `request.TestNames`: `TryResolve` возвращает по одному имени на каждый запрошенный `--test`.
- `HasCategory` повторяет семантику Unity: включения объединяются через ИЛИ, исключения — через И.

```csharp
private static bool HasCategory(string[] requested, List<string> categories)
{
	if (requested == null || requested.Length == 0)
	{
		return true;
	}
	bool hasInclusion = false;
	bool included = false;
	foreach (string category in requested)
	{
		if (IsExclusion(category))
		{
			if (categories.Contains(category.Substring(1)))
			{
				return false;
			}
		}
		else
		{
			hasInclusion = true;
			if (categories.Contains(category))
			{
				included = true;
			}
		}
	}
	return !hasInclusion || included;
}
```

`TryResolve` (проверка `hasCase`) и `TestFilterCoverage` получают новую семантику через `HasCategory` и `SelectCases`, отдельных правок в них нет.

### `AgentTestRunner.cs`

- Новая константа: `private const string ExplicitGuardCategory = "!AgentBridge.ExplicitGuard";`.
- `BuildFilter` отдаёт в Unity категории запроса, а если запрос explicit явно не выбирает — категории плюс `ExplicitGuardCategory`:

```csharp
string[] unityCategories = categoryNames ?? new string[0];
if (!TestNameResolver.SelectsExplicitly(testNames, categoryNames))
{
	var guarded = new List<string>(unityCategories) { ExplicitGuardCategory };
	unityCategories = guarded.ToArray();
}
if (unityCategories.Length > 0)
{
	filter.categoryNames = unityCategories;
}
```

- `TestRunFilter` в `SessionState` (`CoordinatorTestFilterKey`) и `dump.Filter` по-прежнему хранят категории запроса без служебной: сравнение фильтров кэша идёт по тому, что просил агент.
- `CollectEntries`: `entry.ExplicitSkipped = node.ResultState == ExplicitSkippedState;`, где `private const string ExplicitSkippedState = "Skipped:Explicit";`.

### `TestCaseResult.cs`

- Новое поле `public bool ExplicitSkipped;`. В старых дампах его нет, JsonUtility читает `false`.

### `TestFilterCoverage.cs`

- Из последнего `return` в `CoversFilterOnly` выносится `private static bool SameSelection(TestRunFilter filter, TaskRequest request)`: попарный `SetsEqual` для сборок, имён и категорий. `CoversFilterOnly` вызывает его.
- В `Covers` при заполнении `remaining` запись с `ExplicitSkipped == true` пропускается, если не выполнено `SameSelection(dump.Filter, request)`. Так дамп прогона по сборке не отдаст «Skipped» на запрос, который называет explicit-тест по имени. Повтор того же фильтра по-прежнему берётся из кэша: NUnit примет то же решение.
- `Select` не меняется. Explicit-кейсы для запроса без явной выборки уже отсеяны в `SelectCases`.

## CLI (`AgentBridgeCli/`)

### `AllTestsConfirmation.cs`

- `IsUnfiltered` считает прогон нефильтрованным, если нет сборок и имён, а все категории начинаются с `!`:

```csharp
public static bool IsUnfiltered(string[] assemblies, string[] tests, string[] categories)
{
	return assemblies.Length == 0 && tests.Length == 0 && Array.TrueForAll(categories, c => c.StartsWith("!", StringComparison.Ordinal));
}
```

- В `Message` после фразы `No --test, --category or --assembly filter was given.` добавляется: `Excluding categories with --category '!Name' alone still selects the whole suite.`

`--category '!Perf'` проходит разбор в `AgentBridgeApplication.TryParseTests` как обычное значение (не начинается с `--`), отдельных правок разбора нет.

## Тесты

- `AgentBridgeTestSelectionTests` (EditMode), каталог из `Catalog()` с добавленным explicit-кейсом `One.Fixture.Manual` (`Explicit = true`, категория `Showcase`):
	- запрос `AssemblyNames = {"A"}` по дампу со всеми кейсами: `Select` не содержит `One.Fixture.Manual`, `Covers` — `true`, хотя запись `Manual` в дампе отмечена `ExplicitSkipped`;
	- запрос `TestNames = {"One.Fixture.Manual"}` по дампу прогона сборки, где `Manual` — `ExplicitSkipped`: `Covers` — `false`; тот же запрос по дампу, у которого `Filter.TestNames = {"One.Fixture.Manual"}`: `true`;
	- `CategoryNames = {"!Slow"}`: `Select` возвращает только `Case(1)`; `{"Fast", "!Slow"}` — только `Case(1)`; `{"Showcase"}` — `Manual` (включающая категория выбирает explicit явно);
	- `TryResolve` с `TestNames = {"One.Fixture"}`, `CategoryNames = {"!Fast", "!Slow"}` возвращает `no_tests_matched`.
- `AgentBridgeCli.Tests/Program.cs`, `RunAllTestsConfirmationTests`: `IsRequired(none, none, new[] { "!Perf" }, false)` — `true`; `IsRequired(none, none, new[] { "!Perf", "Fast" }, false)` — `false`; `IsRequired(none, none, new[] { "!Perf" }, true)` — `false`.
- Существующие тесты, которые задевает изменение: `AgentBridgeTestSelectionTests`, `AgentBridgeTestCacheTests` (каталог без `Explicit` должен вести себя по-старому).

## Порядок работ

- `TestNameResolver.cs`: поле `Explicit`, `Collect`, `SelectsExplicitly`, `IsExclusion`, `SelectCases`, `HasCategory`.
- `TestCaseResult.cs` и `AgentTestRunner.cs`: `ExplicitSkipped`, служебная категория в `BuildFilter`.
- `TestFilterCoverage.cs`: `SameSelection`, пропуск `ExplicitSkipped` в `Covers`.
- `AllTestsConfirmation.cs`: правило исключающих категорий и текст отказа.
- Тесты из раздела «Тесты».
- Версии: пакет `0.38.0 → 0.39.0`, CLI `1.27.0 → 1.28.0`, плагин `1.34.0 → 1.35.0` (меняется `SKILL.md`). Пересобрать плагин `scripts/build-plugin.ps1`.
- Документация из раздела «Документация».

## Приёмка

- `agentbridge tests --assembly AgentBridge.ProbeTests` не выполняет методы `QueueTimeoutReproTests`, в результате они в `skipped`, `failed = 0`. Подтверждается живым прогоном.
- `agentbridge tests --test QueueTimeoutReproTests.<метод>` по-прежнему выполняет explicit-метод: `scripts/verify-test-cancellation.ps1` проходит.
- `--category '!X'` исключает категорию и в Unity, и в выборке кэша — подтверждают случаи из `AgentBridgeTestSelectionTests`.
- Дамп прогона по сборке не обслуживает из кэша запрос к explicit-тесту по имени — подтверждает тест `Covers`.
- `agentbridge tests --category '!Perf'` без других фильтров отклоняется с `all_tests_confirmation_required` — подтверждает тест CLI.

## Документация

- `README.md`, абзац про `tests --test`: прогон без `--test` и без включающей `--category` пропускает `[Explicit]`; explicit запускается по имени или по включающей категории; `--category '!Name'` исключает категорию и отключает explicit-тесты всего прогона; одни исключения требуют `--confirm-all`.
- `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, раздел `tests`: то же для агента, коротко, с примером `--assembly X --category '!Perf'` и напоминанием брать `!Name` в кавычки в bash.
- `AgentBridgeUnity/Packages/com.elmortem.agentbridge/UNITYAGENT.md`, раздел про `tests`: то же.
- `Docs/PROJECT_MAP.md`: нет.

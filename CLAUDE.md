# CLAUDE.md

Инструкции для агента, работающего в этом репозитории. Пользовательская документация — в `README.md`.

## Что это

Unity Agent Bridge — мост между ИИ-агентом и живым Unity Editor. Агент пишет C#-скрипт или
декларативную задачу, CLI кладёт её в очередь проекта, пакет внутри редактора компилирует скрипт в
памяти через Roslyn (без domain reload, без файлов в `Assets/`) и выполняет на главном потоке,
возвращая логи и результат в stdout.

Три независимо версионируемых компонента:

| Компонент | Где | Версия | Публикация |
|---|---|---|---|
| AgentBridge CLI | `AgentBridgeCli/` | `<Version>` в `AgentBridgeCli/AgentBridgeCli.csproj` | GitHub Release, workflow `agentbridge-cli.yml` |
| Unity-пакет | `AgentBridgeUnity/Packages/com.elmortem.agentbridge/` | `version` в его `package.json` | UPM по git URL, публикация не нужна |
| Плагин агента | `unity-bridge-plugin/` | `version` в `.claude-plugin/plugin.json` | ZIP закоммичен в репозиторий |

## Карта проекта

```
AgentBridgeCli/                     .NET 8 CLI `agentbridge` — клиент моста
AgentBridgeCli.Tests/               Тесты CLI: обычная консоль (Program.cs), без xUnit
AgentBridgeUnity/                   Unity 2022.3.62f2 — хост-проект пакета
  Assets/Tests/Editor/              EditMode-тесты моста (probe, guardrail, arbiter)
  Assets/Tests/PlayMode/            PlayMode-тесты
  Packages/com.elmortem.agentbridge/  ← сам пакет, основная кодовая база
  ProjectSettings/AgentBridge.json  Настройки моста (таймауты, политики сцен, KeepCompletedCount)
unity-bridge-plugin/                Плагин Claude Code: скиллы unity-bridge и unity-ui
Docs/                               Шаблоны UNITYAGENT*.md, заметки, TDD-документы
scripts/                            build-plugin.ps1, fetch-roslyn.ps1, install-agentbridge.ps1/.sh
.github/workflows/                  agentbridge-cli.yml (релиз CLI), release-contract.yml (версии + ZIP)
```

### CLI (`AgentBridgeCli/`)

Команды: `csharp`, `ui`, `sceneshot`, `compile`, `tests`, `play`, `stopplay`, `release`, `wait`,
`status`, `doctor`. Общие флаги: `--project`, `--wait`, `--format json|human`, `--session`, `--note`.
Коды выхода: `0` успех, `1` терминальный отказ задачи (включая `test_failure`), `2` ожидание клиента
исчерпано, `3` проект/мост недоступны или ошибка использования.

- `AgentBridgeApplication.cs` — диспетчер команд
- `CliOptions.cs` — разбор аргументов и валидация флагов
- `BridgeClient.cs` — постановка задачи в очередь и ожидание результата
- `BridgeInspector.cs` — `status` / `doctor`
- `TaskResultFormatter.cs` — вывод `json` (стабильный контракт) и `human`
- `ProjectLocator.cs`, `BridgePaths.cs` — поиск проекта и раскладка `Library/AgentBridge/`

### Пакет (`AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/`)

- Ядро: `AgentBridge.cs` (bootstrap), `TaskCoordinator.cs` (очередь и жизненный цикл задачи —
  самый крупный файл), `AgentSessionScheduler.cs` (ротация между агентскими сессиями)
- Исполнители: `CSharpTaskExecutor.cs`, `CompileTaskExecutor.cs`, `AgentTestRunner.cs`,
  `SceneShot/SceneShotTaskExecutor.cs`, `Ui/UiTaskRunner.cs`
- Компиляция: `RoslynResolver.cs`, `RoslynCompiler.cs`, `ReferenceCatalog.cs`,
  `SourceGuardrail.cs` (отклонение блокирующих и модальных API до исполнения)
- Кэш: `CompileFingerprint.cs`, `CompileCacheStore.cs`, `TestFingerprint.cs`, `TestCacheQuery.cs`,
  `TestRunDumpStore.cs`
- Сцены и плей мод: `SceneSafetyGuard.cs`, `SceneDirtyWatcher.cs`, `AgentSceneManager.cs`,
  `PlaySessionManager.cs`, `PlayModeSceneRecovery.cs`, `UnsanctionedPlayGuard.cs`, `FocusGuard.cs`
- Протокол: `BridgePaths.cs`, `BridgeStatusWriter.cs`, `TaskJournal.cs`, `TaskRecord.cs`
- `Roslyn~/` — вендоренный Roslyn (тильда прячет папку от импорта Unity), обновляется
  `scripts/fetch-roslyn.ps1`; лицензии в `Roslyn~/THIRD-PARTY-NOTICES.md`
- `UNITYAGENT.md` — описание API пакета для агента в чужом проекте

### Плагин (`unity-bridge-plugin/`)

```
.claude-plugin/plugin.json          имя, версия, автор, описание
skills/unity-bridge/SKILL.md        C#-таски, compile, tests, sceneshot, play
skills/unity-ui/SKILL.md            декларативная вёрстка uGUI через *.ui.json
unity-bridge-plugin.zip             собранный артефакт, закоммичен и проверяется в CI
```

## Обязательные правила

### 1. Frontmatter скиллов: `description` ≤ 1024 символов

Полная инструкция и порядок проверки: **[`Docs/rules/skill-frontmatter.md`](Docs/rules/skill-frontmatter.md)**.

Коротко: `description` ≤ 1024 символов, `name` ≤ 64 символа и равен имени каталога скилла, только
однострочные пары `ключ: значение` — никаких блочных скаляров. Иначе агент-хост откажется грузить
скилл с ошибкой `field 'description' in SKILL.md must be at most 1024 characters`, хотя сам ZIP
будет валидным.

Правил недостаточно, поэтому проверка встроена в `scripts/build-plugin.ps1` и валит сборку
(`frontmatter_validation`). **Никогда не обходи её через правку лимитов в скрипте** — режь описание.

### 2. После изменений — поднять версию и собрать плагин

Каждый изменённый компонент обязан увеличить **свою** версию. Это fail-closed: и
`scripts/build-plugin.ps1`, и GitHub Action **Release Contract** отклоняют изменение пакета, плагина
или CLI без большей версии.

Порядок для любой правки:

1. Определи, какие из трёх компонентов задеты (`AgentBridgeCli/*`, `AgentBridgeCli.Tests/*` → CLI;
   `AgentBridgeUnity/Packages/com.elmortem.agentbridge/*` → пакет;
   `unity-bridge-plugin/.claude-plugin/*` или `unity-bridge-plugin/skills/*` → плагин).
2. Подними версию каждого задетого компонента в его файле версии (таблица выше). Багфикс — patch,
   новое поведение — minor.
3. Пересобери плагин — **всегда**, даже если менялся только Unity-пакет или CLI: ZIP закоммичен, и
   сборка заодно прогоняет проверки версий и frontmatter.

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin.ps1
   ```

   Успешный прогон заканчивается `invalid_entries=0` и `zip_validation=PASS`.
   `-ValidateOnly` проверяет, ничего не перезаписывая; `-BaseRef <ref>` задаёт базу сравнения версий
   (по умолчанию `HEAD`, то есть рабочее дерево против последнего коммита).

4. Прогони тесты CLI, если трогал CLI:

   ```bash
   dotnet build AgentBridgeCli/AgentBridgeCli.csproj -c Release
   dotnet run --project AgentBridgeCli.Tests/AgentBridgeCli.Tests.csproj -c Release
   ```

**Никогда не собирай ZIP через `Compress-Archive`.** На Windows он пишет обратные слэши в имена
записей, и потребитель падает с `Zip file contains path with invalid characters`. Канонический
скрипт пишет прямые слэши, отклоняет `\`, абсолютные пути, `..`, дубликаты и запрещённые в Windows
символы, затем сверяет хэш каждого файла в архиве с исходником.

## Соглашения

- Отступы — табы, и в C#, и в PowerShell, и в JSON пакета. `.editorconfig` в репозитории нет.
- Namespace CLI — `AgentBridge.Cli`, file-scoped. Код пакета — namespace `AgentBridge`.
- Пакетный код живёт только в `Editor/`: asmdef `AgentBridge` собирается под `includePlatforms: ["Editor"]`.
- Документы проектирования — `Docs/tdd/YYMMDD-HHMM-TDD-<slug>.md`, по-русски, с первой строкой
  `Status: ...`. Выполненные переезжают в `Docs/tdd/done/`. Заметки — `Docs/notes/YYMMDD-HHMM-NOTE-<slug>.md`.
- Сообщения коммитов — как в истории: `Version <plugin> Unity Agent Version <package> AgentBridge CLI Version <cli>`
  плюс строка с сутью изменения. В сообщение попадают только те версии, которые реально поднимались.
- Ветка разработки — `roslyn-cli`; оба workflow слушают `main` и `roslyn-cli`.
- Артефакты Unity (`Library/`, `Temp/`, `Logs/`, `obj/`) не трогать и не коммитить.

# Разрыв наблюдения на domain reload и чем он закрыт

Заметка к ТДД `260921-1854-TDD-evidence_stat_manifest`. Всё снято в хост-проекте `AgentBridgeUnity`
(Unity 2022.3.62f2, Windows), пакет 0.32.0 → 0.33.0.

## Дефект

PlayMode-прогон перезагружает домен внутри собственного наблюдения. До изменения там было три
независимых дыры, и каждая по отдельности делала `Valid` необоснованным:

1. **Счётчик наблюдателя не переносился.** `DisposeMonitor` умеет складывать `EventCount` в
   `SessionState`, но перед reload его никто не вызывал: монитор умирал вместе с доменом, и всё, что
   он уже успел увидеть, пропадало. Подписки на `beforeAssemblyReload` у `ValidationEvidence` не
   было вовсе.
2. **В самом разрыве не наблюдал никто.** `InputWatchHubLifetime` гасит хаб на `beforeAssemblyReload`,
   новый наблюдатель ставится статическим конструктором уже после reload. Между этими точками входы
   свободны.
3. **Наблюдатель опрашивающий.** Unity подставляет `System.IO.DefaultWatcher` (см.
   `260921-1758-NOTE-observer-install-cost`), события приходят следующим кругом опроса. Поэтому
   терялись и изменения последних секунд *перед* reload, даже когда наблюдатель ещё был жив.

Итог: изменение входа с возвратом внутри окна давало `Evidence: valid` с причиной
`observer reinstalled after N domain reload(s)`. Дайджесты при этом совпадали честно — содержимое
действительно вернулось, — и опереться было не на что.

## Второй свидетель

`InputStatManifest` — путь, размер и mtime по тем же корням, с теми же `excluded` и тем же `ignore`,
что и у дайджеста (обход идёт через тот же `ValidationInputSnapshot.TryCollect`, теперь `internal`).
Снимается на рабочем потоке в подготовке, параллельно второму проходу хеша и уже под наблюдателем;
живёт в `Library/AgentBridge/evidence-manifest-<TaskId>-<попытка>.txt`; сравнивается с таким же
обходом в завершении. Ему не нужен поток, поэтому reload ему безразличен.

Расхождение — событие `input_changes` наравне с событием наблюдателя (в телеметрии у обоих теперь
есть поле `Source`: `observer` или `manifest`). Потерянный или нечитаемый манифест — `Unknown`.
Номер попытки в имени файла нужен, чтобы брошенная попытка не писала в файл текущей.

## Цена

Задача `Task_20260921_193100_000_statcost`, рабочий поток, главный поток измерен до `await`.

| Дерево | Файлов | `InputStatManifest.Capture` | Главный поток | `ValidationInputSnapshot.Capture` |
|---|---|---|---|---|
| `Temp/AgentBridge/ObserverFixture` | 16 004 | 471 мс | 0 мс | 4 462 мс |
| Реальные входы хост-проекта | 438 | 17 мс | 0 мс | 131 мс |

Фикстура накопила 16 004 файла: к 8 000 от прошлого ТДД добавились 8 000 новых с другими именами.
Пропорция от этого не меняется — манифест стоит примерно десятую часть дайджеста по тому же дереву,
и всё это время принадлежит рабочему потоку: главный поток не платит ничего, манифест снимается
параллельно второму проходу хеша.

## Живая проверка разрыва

`Task_20260921_190903_526_f6f84689`, PlayMode `AgentBridgePlayModeProbeTests --fresh`. Через 2 с
после старта из PowerShell менялся **только** mtime входного файла:

```powershell
(Get-Item '.../Assets/Tests/PlayMode/AgentBridgePlayModeProbeTests.cs').LastWriteTimeUtc = [DateTime]::UtcNow
```

Результат — ровно то, что раньше проходило незамеченным:

```
Status: stale_input
Evidence: stale — "an input was modified and restored while the validation ran"
InputDigest == EndInputDigest == 8898b3c9ced2b58f4...
```

Телеметрия по этой задаче — одна строка, и она от манифеста:

```json
{"E":"input_changes","Id":"Task_20260921_190903_526_f6f84689","Events":1,
 "Paths":"D:/.../Assets/Tests/PlayMode/AgentBridgePlayModeProbeTests.cs","Source":"manifest"}
```

Строки с `Source":"observer"` для этой задачи нет вовсе: наблюдатель изменение не увидел. Это и есть
прямое доказательство, что закрыт именно разрыв, а не что-то, что и так ловилось.

## Чистый прогон

Чистый PlayMode-прогон (`Task_20260921_190834_724_b646421b`) даёт `Evidence: valid` с причиной
`observer reinstalled after 1 domain reload(s); the stat manifest covers the gap; both input digests
match`. Ложных срабатываний от файлов, которые пишет сам Unity или Test Framework, не наблюдалось —
расширять `ignore` не потребовалось. Файлов `evidence-manifest-*` в `Library/AgentBridge` не
остаётся ни после завершения, ни после отмены (`Task_20260921_190933_922_23b2fac3`).

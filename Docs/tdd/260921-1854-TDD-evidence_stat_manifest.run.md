# 260921-1854-TDD-evidence_stat_manifest — ход исполнения

## Единицы

- [x] `InputStatEntry.cs`, `InputStatVerdict.cs`, `InputStatManifest.cs` (+ `.meta`), `TryCollect` → `internal`, линки в `AgentBridgeCoordination.Tests.csproj`
- [x] `StatManifestScenarios.cs` + вызов из `Program.cs`
- [x] `ValidationEvidence.cs`: `CarryAcrossReload`, манифест в подготовке, сравнение в завершении, `Source` в телеметрии, `Cleanup` + удаление файлов манифеста
- [x] `AgentBridgeEvidenceTests.StatManifestSeesAChangeThatWasReverted`
- [x] Замер `Capture` на фикстуре и на реальных входах
- [x] Живая проверка разрыва: смена только mtime во время PlayMode-прогона
- [x] Пакет 0.33.0, плагин 1.29.0, сборка ZIP, документация

## Решения

- Предполётная сверка: ни одного файла ТДД не существовало, `ValidationEvidence.cs` совпадал с описанием — план принят без правок.
- Результат `_manifestWork` читается через `Status == RanToCompletion`, а не через `IsFaulted`: отменённая задача иначе бросила бы на `.Result`. Поведение то же, что задумано в ТДД.
- `TryComplete` строит `roots`/`excluded`/`ignore` один раз и делит их между `InputHashJob` и сравнением манифеста — оба свидетеля обязаны описывать одни и те же входы.
- Тест `StatManifestSeesAChangeThatWasReverted` работает во временной папке внутри `Temp` хост-проекта, а не в `Path.GetTempPath()`: ТДД требует Mono-рантайм редактора, а `Temp` исключён из входов и не портит дайджест идущего прогона.
- Правка `SKILL.md` сделала плагин задетым компонентом — версия плагина поднята до 1.29.0 по правилу 2 `CLAUDE.md`.

## Решения за пользователя

- нет

## Проверки

- `dotnet run --project AgentBridgeCoordination.Tests -c Release -- --group all` → `Coordination: PASS`, включая `C12`, `C13`, `C22_observer_hub`, `C23_stat_manifest`.
- `compile` (`Task_20260921_190759_956_c1e495ab`): success, `Evidence: valid`, причина `... the stat manifest covers the gap; both input digests match`.
- EditMode `AgentBridgeEvidenceTests` + `AgentBridgeHashPerformanceTests`: 16/16 зелёных, `Evidence: valid`; повтор той же задачи — `Cached: true`. Финальное подтверждение — `Task_20260921_191319_137_da1e55ea`, 16/16.
- PlayMode `AgentBridgePlayModeProbeTests` (`Task_20260921_190834_724_b646421b`): 2/2, `Evidence: valid`, причина содержит `the stat manifest covers the gap`.
- Живая смена одного mtime во время PlayMode (`Task_20260921_190903_526_f6f84689`): `Status: stale_input`, `Evidence: stale`, дайджесты совпадают, в телеметрии одна строка `input_changes` с `Source=manifest` и этим путём; строки от наблюдателя нет.
- После завершения и после отмены (`Task_20260921_190933_922_23b2fac3`) в `Library/AgentBridge` нет файлов `evidence-manifest-*`.
- Замер (`Task_20260921_193100_000_statcost`): фикстура 16 004 файла — манифест 471 мс на рабочем потоке, 0 мс главного; дайджест по тому же дереву 4 462 мс. Реальные входы, 438 файлов — манифест 17 мс, дайджест 131 мс, главный поток 0 мс. Числа в заметке.
- `scripts/build-plugin.ps1`: `version_check=PASS`, `frontmatter_validation=PASS`, `invalid_entries=0`, `zip_validation=PASS`.

## Замечания

- Ложных `stale` от файлов Unity или Test Framework на чистых прогонах не было — `ignore` не расширялся.
- Фикстура `Temp/AgentBridge/ObserverFixture` накопила 16 004 файла вместо 8 000 из ТДД: прошлый набор остался с ТДД `260921-1758`, новый лёг рядом. Пропорция манифеста к дайджесту от этого не меняется, перегенерировать с нуля не стал.

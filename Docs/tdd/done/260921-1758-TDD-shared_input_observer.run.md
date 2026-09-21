# 260921-1758-TDD-shared_input_observer — ход исполнения

## Единицы

- [x] База: замер установки наблюдателя на фикстуре 8000 файлов
- [x] `InputWatchRoot.cs`, `InputWatchHub.cs`, переписанный `ValidationInputMonitor.cs`, линки в оба csproj
- [x] `ObserverHubScenarios.cs` + вызов из `Program.cs`
- [x] `CachedResultServer.cs`, `TestRunCoalescer.cs`, `InputWatchHubLifetime.cs`
- [x] Повторный замер после изменения
- [x] Версия пакета 0.32.0, сборка плагина, документация

## Решения

- Предполётная сверка: расхождений между ТДД и кодом не найдено, все имена и сигнатуры совпали.
- `InputWatchRoot._subscribers` объявлен `volatile`: `Dispatch` и `OnError` читают массив без блокировки, публикация нового массива должна быть видна потоку наблюдателя.
- `Record` проверяет `_disposed` и до фильтров, и под тем же локом, где считает: закрытое окно не платит за чужие фильтры и не может посчитать событие после закрытия.
- В `C22` перед проверкой доживания хаб выметается: `WatcherCount` общий на весь процесс, и корни предыдущих случаев набора его завышали.
- Диагностика по ходу базы: Unity подставляет опрашивающий `System.IO.DefaultWatcher`, а не Win32-наблюдатель — отсюда и цена установки, и задержка доставки в секунды. Записано в заметку.

## Решения за пользователя

- нет

## Проверки

- Консоль: `AgentBridgeCoordination.Tests --group all` → `Coordination: PASS`, включая `C13` и новый `C22_observer_hub`; `AgentBridgeCompile.Tests` → 11/11.
- Замер до (пакет 0.31.1, фикстура 8000 файлов): установка на главном потоке 735 мс, второй монитор того же корня ещё 702 мс.
- Замер после (пакет 0.32.0, та же фикстура): холодный `OpenAsync` — 0 мс главного потока (581 мс на рабочем), синхронный конструктор на тёплом хабе — 0 мс, реальные корни — 3 мс. Приёмочный порог 5 мс выдержан с запасом.
- Unity EditMode `AgentBridge.ProbeTests` целиком: 104/104 прошли (дважды). Вердикт `stale_input` — см. «Замечания».
- Unity EditMode `AgentBridgeEvidenceTests` + `AgentBridgeHashPerformanceTests`: 15/15, `Evidence: valid`; повтор той же нефрешевой задачи — `Cached: true`, `Evidence.Validity = valid`.
- Unity PlayMode `AgentBridge.PlayModeProbeTests`: 9/9, `Evidence: valid` (наблюдатель переставлен после domain reload, дайджесты совпали); вторая подходящая задача во время прогона получила `Status: attached`.
- `scripts/build-plugin.ps1`: `version_check=PASS`, `frontmatter_validation=PASS`, `invalid_entries=0`, `zip_validation=PASS`.

## Замечания

- `tests --mode EditMode --assembly AgentBridge.ProbeTests` целиком всегда даёт `stale_input`: сам набор создаёт и убирает `Assets/AgentBridgeMissingMetaProbe.cs`, `Assets/AgentBridgePrefabStageProbe.prefab` и переписывает `ProjectSettings/AgentBridge.json` (пути названы телеметрией `input_changes`). Свойство набора, а не хаба: снимок входов до и после различался ещё до этой правки. Тесты при этом зелёные. Материал для отдельного ТДД: либо объявить эти пробы фикстурой, либо уводить их из корней входов.
- Рабочее дерево несёт `AgentBridgeUnity/ProjectSettings/AgentBridge.json` без устаревшего `TaskTimeoutSeconds` — редактор переписал файл сам при запуске; к этой правке отношения не имеет.
- `AgentBridgeCompile.Tests/bin/Release/net8.0/AgentBridgeCompile.Tests.dll` лежит в git и меняется от любой сборки набора. Артефакт в репозитории — отдельный вопрос.
- Побочная польза изменения: до него ежесекундная проверка кеша ставила и сносила наблюдатель внутри одной секунды, а опрашивающий наблюдатель на большом дереве отдаёт первое событие за секунды. То есть на крупном проекте условие `monitor.EventCount == 0` в `CanPublish` выполнялось само собой. Общий наблюдатель живёт между проверками, и второе свидетельство наконец работает.

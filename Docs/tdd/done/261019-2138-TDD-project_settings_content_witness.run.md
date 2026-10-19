# Ход исполнения: 261019-2138-TDD-project_settings_content_witness

## Единицы

- [x] `InputStatEntry.Hash`, `InputStatVerdict.Restored/RestoredPaths`, `ValidationInputSnapshot.ToHex` → `internal`
- [x] `InputStatManifest`: перегрузка `Capture` с `contentRoot`, формат `ticks|length|hash|path`, ветка `Restored` в `Compare`
- [x] `ValidationEvidence`: `ContentWitnessedRoot`, проводка в `InstallMonitor`, `StartManifest`, `TryComplete`, телеметрия `input_restored`
- [x] `CacheLookupWitness`, `CachedResultServer`, `TestRunCoalescer`: `contentRoot` и исключение корня из наблюдателя
- [x] Сценарий `ASettingsRewriteWithSameBytesIsRestored`
- [x] Версии: пакет 0.42.1, плагин 1.38.1 (правка SKILL.md); README, PROJECT_MAP, CLAUDE.md, SKILL.md, UNITYAGENT.md

## Решения

- `ObserverExcludedRoots` лежит в `InputStatManifest`, а не в `ValidationEvidence`: `CacheLookupWitness` компилируется в `AgentBridgeCoordination.Tests`, где нет `ValidationEvidence` (Unity API).
- `CacheLookupScenarios` передаёт `contentRoot = ""` в оба вызова `CacheLookupWitness.OpenAsync`.
- `SKILL.md` и `UNITYAGENT.md` дополнены тем же правилом: контракт `stale_input` для агента изменился. Отсюда bump плагина.

## Проверки

- `AgentBridgeCoordination.Tests --group all`: `Coordination: PASS`.
- `scripts/build-plugin.ps1`: `version_check=PASS`, `zip_validation=PASS`.
- Unity-хост: `compile` success, EditMode `AgentBridgeEvidenceTests` 11/11.
- Живая приёмка: в хосте нет Shader Graph, поэтому перезапись воспроизведена через `touch ProjectSettings/QualitySettings.asset` посреди PlayMode `ResponsiveLongRun`. Результат: success, `Evidence: valid`, в телеметрии `input_restored` с путём `QualitySettings.asset` и без `input_changes`.

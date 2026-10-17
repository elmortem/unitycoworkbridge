# 261017-2255-TDD-skip_explicit_tests — ход исполнения

## Единицы

- [x] `TestNameResolver.cs`: `Explicit`, `Collect`, `SelectsExplicitly`, `IsExclusion`, `SelectCases`, `HasCategory`
- [x] `TestCaseResult.cs`, `AgentTestRunner.cs`: `ExplicitSkipped`, служебная категория в `BuildFilter`
- [x] `TestFilterCoverage.cs`: `SameSelection`, пропуск `ExplicitSkipped` в `Covers`
- [x] `AllTestsConfirmation.cs`: исключающие категории, текст отказа
- [x] Тесты EditMode и CLI
- [x] Версии: пакет 0.39.0, CLI 1.28.0, плагин 1.35.0; сборка плагина
- [x] Документация: README, SKILL.md, UNITYAGENT.md

## Решения

- `SelectsExplicitly` возвращает `false` при любой `!`-категории: по факту ТДД `NotFilter` в `AndFilter` гасит explicit и у названного по имени теста, иначе выборка кэша расходилась бы с Unity.
- `TryResolve` (`hasCase`) тоже учитывает `Explicit`: `--test X --category '!Y'`, где X — только explicit, даёт `no_tests_matched`, а не пустой прогон.
- Explicit-кейс добавлен отдельным `ExplicitCatalog()`, а не в общий `Catalog()`: иначе существующие тесты покрытия фикстуры по имени ломаются.
- `SameSelection` терпит `filter == null` (старые дампы).

## Решения за пользователя

- нет

## Упрощения

- нет

## Проверки

- CLI: `AgentBridgeCli.Tests: PASS`.
- Компиляция Unity: success.
- EditMode `AgentBridgeTestSelectionTests` + `AgentBridgeTestCacheTests`: 20/20.
- EditMode `--assembly AgentBridge.ProbeTests --fresh`: 122 passed, 0 failed, 4 skipped (explicit `QueueTimeoutReproTests`); `stale` дважды при совпадающих отпечатках до/после — принято.
- `scripts/verify-test-cancellation.ps1` (explicit по имени): PASS все сценарии.
- `scripts/build-plugin.ps1`: `version_check=PASS`, `invalid_entries=0`, `zip_validation=PASS`.

## Замечания

- нет

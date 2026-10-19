# 261019-1925-TDD-playmode_compile_gate — ход исполнения

## Единицы

- [x] `CompileTaskExecutor.LastCycleErrors()`
- [x] `TaskCoordinator`: отказ PlayMode при `scriptCompilationFailed`, `RefusePlayModeOnCompileErrors`, `PlayModeCompileGateMessage`
- [x] `SKILL.md`: абзац в `tests`, пункт 6 форс-мажора
- [x] `README.md`, `UNITYAGENT.md`
- [x] Версии: пакет 0.42.0, плагин 1.38.0; плагин пересобран
- [x] Живая приёмка в `AgentBridgeUnity`

## Решения

- В рабочем дереве уже лежала незакоммиченная работа по модальным окнам с поднятыми версиями (0.41.0 / 1.37.0) → поднял ещё на minor, чтобы правку можно было закоммитить отдельно.
- `SKILL.md`: запрет «чужой код без согласования не меняй» и пункт 5 («сообщи пользователю») противоречили пункту 6 → оба сослал на пункт 6.
- ТДД и run-файл перенесены в `Docs/tdd/done/` по соглашению репозитория.

## Решения за пользователя

- нет

## Упрощения

- нет

## Проверки

- build-plugin.ps1: version_check, frontmatter_validation PASS, invalid_entries=0, zip_validation=PASS.
- compile с `Assets/CompileGateProbe.cs` → compiler_error, CS0029 в строке 5.
- PlayMode `AgentBridgePlayModeProbeTests` при красной компиляции → compiler_error, ForeignErrors true, Diagnostics с файлом и строкой, лог `playmode_blocked_by_compile_errors ... CompileGateProbe.cs`, TotalMs 7, код выхода 1.
- Телеметрия: task_finish отказанной задачи → task_start следующей через 0,5 с.
- EditMode `PlayModeGuardrailTests` при красной компиляции — 5/5.
- После удаления пробника: compile success; PlayMode `AgentBridgePlayModeProbeTests` — 2/2.

## Замечания

- `ModalDialogProbeTests` целиком `[Explicit]`: прогон по имени класса без имён тестов даёт 2 skipped и `success`.

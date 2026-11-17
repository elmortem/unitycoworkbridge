# 261117-1042-TDD-compile_fingerprint_hash_cache — ход исполнения

## Единицы

- [x] `CompileFingerprint`: перегрузка с `InputFileHasher`, `Current()` с кэшем, комментарии
- [x] `CompileInputContext.HashCache` и `StartCapture`, заглушки в обоих стендах
- [x] C27 дополнен файлом `c.cs` и шагами отпечатка
- [x] Пакет 0.45.0, README, PROJECT_MAP, `build-plugin.ps1`

## Решения

- C27: шаги отпечатка идут на экземпляре кэша из шага «reloaded», чтобы проверить и чтение с диска, и то, что `Commit` отпечатка не вычеркнул записи дайджеста.

## Решения за пользователя

- нет

## Упрощения

- нет

## Проверки

- `AgentBridgeCompile.Tests`: 11/11.
- `AgentBridgeCoordination.Tests --group all`: PASS с `HashPlatform=UNITY_EDITOR_WIN` (C27 покрыт) и без него.
- Unity compile: success; `input-hash-cache.txt` содержит `.cs` пакета `com.elmortem.agentbridge`.
- EditMode `AgentBridge.ProbeTests`: 128 passed, 6 skipped, 0 failed дважды; оба прогона `stale_input` с одинаковым отпечатком входов `6c9293e7…`, результат принят.
- `build-plugin.ps1`: `version_check=PASS`, `invalid_entries=0`, `zip_validation=PASS`.

## Замечания

- EditMode-прогон `AgentBridge.ProbeTests` стабильно даёт `stale_input` («input was modified and restored»): вероятно, сами тесты меняют и восстанавливают входы. Отдельная задача.

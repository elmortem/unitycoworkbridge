# Ложная блокировка отмены оставшимся PlayMode-контроллером

## Причина

WaterWalk, Unity 6000.4.0f1, пакет 0.26.0: задача
`Task_20260913_124648_701_917a6763` удерживала очередь в `cancel_blocked`,
хотя `IsPlaying=false`. Диагностика отмены заканчивалась
`Waiting: Unity PlayMode controller object remains`.

Эта причина возвращается только после отрицательных проверок активности TestRunner API
и сохранённых `TestJobDataHolder.TestRuns`. Мост ошибочно считал само наличие
`PlaymodeTestsController` среди `Resources.FindObjectsOfTypeAll` признаком исполнения.
Поиск включает оставшиеся объекты и загруженные ассеты. В установленном в WaterWalk
Test Framework этот контроллер — MonoBehaviour без ExecuteAlways/ExecuteInEditMode;
его тестовая корутина не исполняется в Edit Mode.

## Воспроизведение и исправление

В свободном хосте Unity 2022.3.62f2 через C#-задачу создан временный контроллер с
HideAndDontSave, без запуска теста и без Play Mode. До создания `RunningReason()` пуст,
с контроллером — `Unity PlayMode controller object remains`, после удаления — снова пуст.
Задача: `Task_20260913_160714_360_controller_before`.

В 0.26.1 проверка наличия PlayMode-контроллера действует только в Play Mode или при
входе в него. Проверки активных заданий Unity и EditMode runner сохранены; восстановление
сцен и подтверждение завершения по-прежнему предшествуют освобождению очереди.
Исправление не удаляет контроллеры, журналы или задачи и не перезапускает редактор.

## Проверки

- Свежая компиляция: `Task_20260913_130738_565_e04d9c6e`, success.
- `scripts/verify-inert-test-controller.ps1`: PASS, активный и неактивный временный
  контроллер не блокируют Edit Mode, удаление в finally.
  Задача: `Task_20260913_160813_929_inert_controller`.
- `TestCancellationPolicyTests`: 9 passed, 0 failed, Evidence valid;
  включая проверку, что настоящий EditMode-прогон остаётся активным.
  Задача: `Task_20260913_130815_913_16ab99df`.
- `scripts/verify-test-cancellation.ps1`: PASS — таймаут EditMode, восстановление сцены,
  таймаут PlayMode, оставшееся восстановление завершённого владельца, отмена ожидающей
  задачи и чужого активного теста, повторная отмена, кооперативная отмена C#.
  После остановки тестов следующие задачи успешно выполнялись.
  Артефакты: `AgentBridgeUnity/Temp/AgentBridge/repro-queue/20260913_160824_493`.
- Каноническая сборка ZIP: version_check, frontmatter_validation, zip_validation PASS.

Живые проверки выполнены в хосте 2022.3.62f2. В WaterWalk подтверждены диагностика и
исходники установленного Test Framework; обновление его пакета и фактическое снятие
текущей блокировки этой проверкой не подтверждены.

Версии: Unity package 0.26.1, plugin 1.23.1, CLI 1.18.1.

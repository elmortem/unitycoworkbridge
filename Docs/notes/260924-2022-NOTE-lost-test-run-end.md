# Потерянный конец тестового прогона (WaterWalk, 24.09.2026)

## Инцидент

`Task_20260924_191012_962_d707abe5` (WaterWalk, Unity 6000.4.0f1, Test Framework 1.6.0, пакет
0.34.0) зависла навсегда. В 19:47:18 `CancelTestRun` вернул `false` — джоба в
`TestJobDataHolder` уже не было, — а `RunningReason()` бесконечно отдавал
`Unity EditMode runner object remains`. Очередь встала в `cancel_blocked` без шанса выйти:
отменять было нечего, а признак активности не исчезал.

## Причина

`EditModeRunner` — ScriptableObject, и уничтожается он ровно в одном месте:
`RunFinishedInvocationEvent.Execute` вызывает `testJobData.editModeRunner.Dispose()`. На путях
ошибки (`RunFailed` → `IErrorCallbacks.OnError`, `RunOnError = DoNotRunOnError`) и внутреннего
`StopRun` это событие не выполняется. Объект остаётся, переживает domain reload и навсегда врёт
о живом прогоне. Наличие `EditModeRunner` — не признак активности, а признак того, что прогон
кончился не по-человечески.

Вторая половина причины: без `RunFinished` задачу никто не завершал. `RunStarted`/`RunFinished`
были единственным источником исхода, а `IErrorCallbacks` мост не реализовывал.

## Что сделано (пакет 0.34.1)

- `RunningReason()` больше не смотрит на объект `EditModeRunner`. Живой прогон закрывают
  `IsTestRunActive`/`IsRunActive` и сохранённые `TestJobDataHolder.TestRuns[].isRunning`,
  включая resume после reload.
- `TestCallbacks` реализует `IErrorCallbacks`: `OnError` переводит задачу в `runtime_error`.
- `TestRunLifecycle` ведёт `InactiveSince` и через `LostRunGraceMs` = 25 с завершает прогон,
  вокруг которого не осталось никакой активности, — страховка на случай, когда джоб исчезает
  вообще без колбэков.
- Отмену у Unity (`CancelTestRun`) запрашиваем только для исхода `canceled`: у `runtime_error`
  джоб доигрывает cleanup режима Error, и отмена его прервёт.

## Разрывы тиков — не блокировка экрана

Попутно проверено предположение, что долгие разрывы тиков (до 240 с) связаны с блокировкой
экрана или потерей фокуса. Нет: 100 из 103 разрывов длиннее 30 с за 20–24.09 пришлись на задачи
`tests`, одинаково в фокусе и без него. Это долгие синхронные тесты, которые не отдают главный
поток, а не сон редактора.

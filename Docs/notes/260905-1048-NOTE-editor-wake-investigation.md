# Пробуждение Unity: причины, исправление и проверка

Дата: 2026-09-05. Изменения: Unity package 0.22.1, CLI 1.15.1, plugin 1.19.1.

## Что установлено

1. `AgentBridgeApplication.RunAsync` возвращал отказ при устаревшем heartbeat до создания
   `BridgeClient`. Поэтому watchdog в `WaitForTaskAsync` вообще не работал для редактора,
   который перестал тикать **до** подачи команды.
2. `EditorTickPump` вызывал `SignalTick` только внутри `EditorApplication.update`. Его
   Win32-таймер был `SetTimer(NULL, ..., NULL)`: он доставлял сообщение в очередь ОС, но не
   выполнял `SignalTick`. Успешный SetTimer и PostMessage не доказывают обновление Unity.
3. `AgentBridge.Stop` снимал таймер, однако следующий `EditorTickPump.OnUpdate` устанавливал
   его снова, не проверяя Enabled.
4. В Unity 6.4 обнаружено включение моста в AssetImportWorker: `[AgentBridge] Enabled.`
   присутствовал в логах обоих workers тестового проекта. Рабочие процессы разделяют Library
   с основным редактором, но не его SessionState. `FinalizeOrphanRecords` мог пометить активный
   тест как прерванный. Это воспроизвелось: CLI получил `interrupted_by_domain_reload`, а позже
   основной редактор записал `success` в ту же задачу.

В исходном журнале WaterWalk за 5 сентября подтверждены настоящие `bridge_asleep`: несколько
команд получили пять `post` и один `focus`, затем отказ. Строки `tick_gap` также показывают
паузы при `HasWork=true, Focused=false`. Эти данные подтверждают симптом; они не позволяют
объявить каждую паузу сном, а не импортом, компиляцией, диалогом или блокировкой пользовательского кода.
Активные задачи WaterWalk в ходе проверки не изменялись.

## Почему выбран этот механизм

В официальной ветке Unity 2022.3 `EditorApplication.SignalTick` помечен `[ThreadSafe]`:
[UnityCsReference, EditorApplication.bindings.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/EditorApplication.bindings.cs).
Unity использует этот вызов для запроса внутреннего тика из
[CallDelayed](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/EditorApplication.cs).
Документация [Microsoft SetTimer](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-settimer)
гарантирует сообщение WM_TIMER при NULL callback; обещания выполнить цикл Unity там нет.

`BackgroundTickTimer` вызывает только заранее привязанный SignalTick. У него нет постоянного
потока с циклом, файлового ввода-вывода, чтения настроек Unity, SynchronizationContext.Send,
ожидания главного потока или Join. Перед reload/quit Dispose под тем же lock дожидается конца
короткого сигнала; запланированные callbacks после этого пропускают вызов. Исключение из сигнала
перехватывается внутри callback, таймер отключается, диагностика публикуется на главном потоке.
Heartbeat остаётся доказательством прогресса именно основного цикла, а не фонового потока.

Таймер ставится до первого update. Интервал в работе — ActiveTickIntervalMs, в простое —
IdleTickIntervalMs. Native message timer оставлен как ограниченный fallback при отсутствии/отказе
приватного API. Batch mode не запускает coordinator, test callbacks, scene recovery и wake timer.

CLI допускает устаревший heartbeat только для живого, включённого, совместимого локального
редактора с совпавшим проектом. Чужой PID, выключенный мост и прочие операционные ошибки не
обходятся. Попытки начинаются с возраста heartbeat 5 секунд. Пять сообщений и один focus
ограничивают вмешательство; восстановление ограничено 120 секундами без прогресса heartbeat,
включая редактор в фокусе. Телеметрия Sent означает результат native-вызова, не успешное пробуждение.

## Выполненная проверка

Все команды исполнялись через CLI, собранный из рабочего дерева. Unity-проверки — через мост.
Unity 2022.3.62f2: собственный AgentBridgeUnity. Unity 6000.4.0f1: изолированный минимальный проект
Temp/WakeUnity6 с локальной ссылкой на тот же пакет и Test Framework 1.6.0.

| Проверка | Результат / задача |
|---|---|
| CLI Release build | 0 ошибок, 0 предупреждений |
| CLI tests | PASS: admission при устаревшем heartbeat, запреты для чужого/невалидного редактора, foreground, ограничение ожидания, сброс по heartbeat |
| BackgroundTickTimer | Без основного цикла вызывает сигнал; Dispose ждёт активный callback; после Dispose нет вызовов; исключение не выходит в ThreadPool |
| Unity 2022.3 compile | success, Task_20260905_104555_903_31f0b106 |
| Unity 2022.3 wake tests | 6/6, Task_20260905_104756_469_831eb8f2 |
| Unity 2022.3 PlayMode / reload | 2/2, Task_20260905_104606_408_6eb2f4e3 |
| Unity 6.4 compile | success, Task_20260905_104629_802_052dbb97 |
| Unity 6.4 wake tests | 6/6, Task_20260905_104632_792_a5d67d32 |
| Unity 6.4 PlayMode после worker fix | 2/2, Task_20260905_104440_806_06f5bb37; повторно 2/2, Task_20260905_104635_783_91d80f09 |
| Unity 6.4, свёрнутое окно | Task_20260905_104000_WakeProbe: 242 update и 242 сигнала за 8 секунд наблюдения, max gap 36 ms, minimized=true, focused=false |
| Плагин | version_check=PASS, frontmatter_validation=PASS, invalid_entries=0, zip_validation=PASS |

Проверка свёрнутого окна использовала IsIconic и наблюдение EditorApplication.update; компиляция,
EditMode и PlayMode затем запускались в том же свёрнутом редакторе. Проверка wake tests отдельно
отключает именно EditorTickPump.OnUpdate и убеждается, что независимые сигналы продолжаются.

До worker fix задача Task_20260905_104135_698_6c48d9b2 получила ложный terminal
interrupted_by_domain_reload, затем была переписана в success. После fix этот сценарий прошёл дважды.

## Ограничения доказательств

- Общая EditMode-сборка на 2022.3 не полностью зелёная: последний прогон
  Task_20260905_104601_000_26284893 — 37 passed / 5 failed из-за IOException Win32 1224 при
  перезаписи ProjectSettings/AgentBridge.json в тестах сцен. Это не скрыто и не засчитано за PASS.
- Чтобы отделить ошибку настроек от нового таймера, в тестовом редакторе восстановлен прежний
  backend `thread`. Task_20260905_104735_589_19589d67 повторил ту же блокировку: 5 passed / 12 failed
  из 17 тестов AgentBridgeProbeTests. Затем background_signal восстановлен, wake tests снова 6/6.
  Отдельный ранее выполненный запуск AgentBridgeProbeTests прошёл 17/17; ошибка непостоянная.
- Реальная блокировка main thread и модальные диалоги сигналом не устраняются.
- macOS/Linux не проверялись в редакторе. ThreadPool backend не зависит от Windows, но это
  свойство реализации, а не runtime proof на других платформах.
- Пакет и установленный CLI в WaterWalk не обновлялись: там работали другие агентские сессии.
  Исправление подготовлено и проверено в этом репозитории; публикация не выполнялась.

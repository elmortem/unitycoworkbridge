Status: Не готов

# Дисциплина использования play — инструкция + механика

Проблема: агенты запускают `agentbridge play` без нужды, забывают `stopplay` и блокируют редактор параллельным сессиям. Команды `play`/`stopplay` остаются, но скилл получает явные правила «когда можно / когда нельзя / как останавливаться», а мост подкрепляет их механикой: `play` без `--note` отклоняется, дефолт длины сессии снижается со 120 до 30 секунд.

## References (not inlined)

- Конвенции кода: CLAUDE.md проекта (табы, типы в отдельных файлах).
- Текущее поведение: `AgentBridgeCli/AgentBridgeApplication.cs` (case "play"), `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/PlaySessionManager.cs` (`BeginPlay`), `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/AgentBridgeSettings.cs`.
- Скилл: `unity-bridge-plugin/skills/unity-bridge/SKILL.md`, раздел `### play [--seconds N] --session <id> и stopplay [--session <id>]`.

## AgentBridgeCli/AgentBridgeApplication.cs

В `case "play":` добавить проверку `--note` рядом с проверкой `--session`. Итоговый вид case:

```csharp
			case "play":
				if (commandArguments.Length != 0 || options.Session == null)
				{
					return WriteError("bad_usage", "usage: agentbridge play [--seconds N] --note <intent> --session <id> [--project <path>] [--wait <seconds>]", options.Format);
				}

				if (string.IsNullOrWhiteSpace(options.Note))
				{
					return WriteError("bad_usage", "play requires --note with the intent of the session (what to check and why)", options.Format);
				}

				return await client.SubmitPlayAsync(options.Seconds, options.WaitSeconds);
```

В `WriteHelp()` заменить строку команды `play`:

```
  play [--seconds N] --note <intent> --session <id>   open a play session; only csharp and sceneshot run inside it
```

и строку опции `--seconds`:

```
  --seconds <n>      play session length; defaults to the editor setting (30)
```

## Editor/PlaySessionManager.cs

В `BeginPlay`, сразу после проверки `request.AgentSessionId`, добавить:

```csharp
			if (string.IsNullOrWhiteSpace(request.Note))
			{
				error = "play requires --note with the intent of the session";
				return false;
			}
```

## Editor/AgentBridgeSettings.cs

Заменить значение по умолчанию:

```csharp
		public int PlaySessionDefaultSeconds = 30;
```

Существующий `ProjectSettings/AgentBridge.json` хранит старое значение — на таких проектах дефолт меняется вручную через **Tools → Agent Bridge → Setup...**; код это не мигрирует.

## unity-bridge-plugin/skills/unity-bridge/SKILL.md

Раздел `### play [--seconds N] --session <id>` и `stopplay [--session <id>]` заменить целиком (от заголовка до строки про `agentbridge status` включительно) на:

~~~markdown
### `play [--seconds N] --note <intent> --session <id>` и `stopplay [--session <id>]`

Единственный законный способ запустить плей мод — и самый дорогой инструмент моста: пока твоя play-сессия жива, редактор закрыт для всех остальных агентов. Плей мод — это не «посмотреть, как оно», а короткое измерение по заранее сформулированному плану.

**Когда `play` уместен** — только если на вопрос отвечает именно рантайм:

- пользователь прямо попросил проверить поведение в игре или снять геймплей;
- нужен game-view скриншот (`sceneshot` с `"view": "game"`);
- баг воспроизводится только в плей моде, и это надо подтвердить или опровергнуть.

**Когда `play` запрещён** — для всего, у чего есть свой инструмент:

- «проверить, что компилируется» — `compile`;
- «проверить, что логика работает» — `tests`;
- «посмотреть сцену или уровень» — `sceneshot` с `"view": "scene"`;
- «посмотреть UI» — `uishot` из скилла `unity-ui`;
- любопытство без конкретного вопроса, на который отвечает рантайм, — плей не запускается вовсе.

**Протокол сессии.** До `play` сформулируй план: что именно снять или проверить и сколько секунд это займёт. Затем:

- `--note` обязателен — короткое намерение сессии («game-шот главного меню», «проверить спавн третьей волны»); без него `play` отклоняется, а соседние сессии видят твой note и понимают, зачем занят редактор;
- `--seconds` указывай всегда и минимально (обычно 15–60); без него берётся дефолт из настроек редактора (30 с), максимум в любом случае 600 с;
- собрал доказательства — сразу `stopplay`, не досиживай дедлайн;
- один вопрос — одна сессия; не держи плей «на всякий случай» между проверками.

```bash
agentbridge play --seconds 30 --note "game-шот главного меню" --session AB_20260813_1500_a1f
agentbridge stopplay --session AB_20260813_1500_a1f
```

`play` открывает play-сессию, владельцем которой становится твоя `--session` (она обязательна), и возвращает `success` с `ReturnValue` вида `playing_until:<UTC>`.

- Внутри **своей** play-сессии тебе доступны только `csharp` и `sceneshot` (включая `"view": "game"`). Любой другой kind отклоняется с `kind not allowed during play session`.
- Чужую play-сессию остановить нельзя: `stopplay` вернёт `rejected` с `play_session_held_by:<id>`. Дождись, пока владелец закончит или истечёт дедлайн.
- Плей мод, оставшийся без сессии (застрявший таск, ручной запуск), может погасить **любой** агент: `stopplay` вернёт `success` с `stopped:manual`.
- `stopplay` вне плей мода — безобидный no-op: `success`, `ReturnValue: "not_playing"`.
- Сессия завершается сама по дедлайну; отдельный `stopplay` для этого не нужен, но и не вреден.
- Пока идут тесты (`tests --mode PlayMode`), `play` и `stopplay` отклоняются с `tests are running`.
- Если человек нажал Stop в редакторе, сессия закрывается сама, а в логах появляется `play session ended externally`.
- Если агентский таск всё же прорвался в плей мод в обход guardrail, мост выходит из него автоматически и дописывает в журнальную запись виновника строку `this task entered play mode; the bridge exited it automatically`. Человеческий плей мод мост не трогает никогда.

`agentbridge status` показывает состояние: `IsPlaying`, `PlaySessionAgentId`, `PlaySessionDeadlineUtc` (в `--format human` — строка `Playing:`).
~~~

## Проверка

- `agentbridge play --session X` без `--note` → `bad_usage` из CLI.
- Task-файл kind `play` с пустым `Note`, положенный в inbox напрямую → `rejected` с `play requires --note with the intent of the session`.
- `agentbridge play --note "..." --session X` без `--seconds` на чистом проекте → `ReturnValue: playing_until` через ~30 с от старта.
- `agentbridge compile` и `agentbridge tests` работают без `--note` как раньше.

## После выполнения

- Смени статус в начале документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию проекта под эти изменения.

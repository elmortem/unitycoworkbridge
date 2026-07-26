---
name: unity-ui
description: "Use this skill for any uGUI layout work in a Unity project via Agent Bridge — creating or editing UI prefabs (RectTransforms, Images, TMP texts, Buttons, custom view components), inspecting layout geometry, and screenshotting UI prefabs. Works through declarative Task_*.ui.json tasks: no C# compilation, no domain reload, iterations take seconds. Trigger on any layout phrasing: 'сверстай экран/попап', 'поправь вёрстку', 'подвинь/выровняй/перекрась элемент', 'создай UI-префаб', 'скриншот экрана', 'что где стоит на экране'. Do NOT use for gameplay logic, non-UI editor operations (use unity-bridge), or UI Toolkit (not supported — uGUI + TMP only)."
---

# Unity UI — декларативная вёрстка uGUI

Вёрстка через декларативные задачи Agent Bridge: пишешь `<TaskName>.ui.json` во временный файл, выполняешь установленную в `PATH` команду `agentbridge ui <файл>`, мост применяет его к префабу без компиляции и без domain reload, возвращает результат в stdout. Если GUI-агент ещё не подхватил обновлённый `PATH`, используй `%LOCALAPPDATA%\AgentBridge\bin\agentbridge.exe` в Windows или `$HOME/.local/bin/agentbridge` в macOS/Linux; не ищи CLI внутри UPM-пакета. CLI находит Unity-проект от текущей директории вверх; вне проекта используй `--project <path>`.

## Quick reference

| Шаг | Действие |
|-----|----------|
| 1 | Выполнить `agentbridge status`; если cwd вне проекта — повторить с `--project <path>` |
| 2 | Найти `UNITYAGENT-UI.md` в проекте (или устаревшее `UNITYCOWORK-UI.md`) — соглашения вёрстки |
| 3 | Незнакомый префаб — сначала задача с `dump`, изучить `Library/AgentBridge/Artifacts/<TaskId>/uidump.json` |
| 4 | Итерация: задача `[apply..., shot]`, затем `agentbridge ui <файл>` |
| 5 | Посмотреть PNG и `rects.json` в `Library/AgentBridge/Artifacts/<TaskId>/`, продолжить итерации |

Имя задачи: `Task_YYYYMMDD_HHMMSS`, временный файл `<TaskName>.ui.json` — имя файла становится `TaskId`. Результат — один JSON в stdout (`TaskRecord`), статусы `success`/`runtime_error`/`rejected`/`timeout`; список созданных файлов — в поле `Artifacts`. Все временные UI-артефакты принадлежат задаче и лежат только в `Library/AgentBridge/Artifacts/<TaskId>/`; авто-трим по `KeepCompletedCount` удаляет этот каталог вместе с задачей.

## Формат задачи

```json
{
	"prefab": "Assets/Resources/Prefabs/UI/MyScreen.prefab",
	"actions": [
		{ "action": "apply", "target": "Popup", "node": {
			"rect": { "anchorMin": [0.5, 0.5], "anchorMax": [0.5, 0.5], "pos": [0, 0], "size": [600, 400] },
			"components": [ { "type": "Image", "sprite": "Assets/Sprites/UI/PopUp_Window.png", "imageType": "Sliced", "color": "#FF005A" } ],
			"children": [
				{ "name": "Title", "rect": { "anchorMin": [0, 1], "anchorMax": [1, 1], "pos": [0, -40], "size": [0, 60] },
					"components": [ { "type": "Text", "text": "ЗАГОЛОВОК", "size": 42, "color": "#FF005A", "align": "Center" } ] }
			]
		} },
		{ "action": "shot", "outline": ["Popup"] }
	]
}
```

- Если префаба нет — он создаётся (корень stretch 0..1).
- Порядок: все `apply`/`delete` → одно сохранение → `dump`/`shot`.

## Семантика apply

- `target` — путь от корня (`"Popup/Title"`, дубли имён — `Item[2]`), `""` — сам корень. Отсутствующие сегменты создаются пустыми RectTransform.
- Указанные свойства ставятся, неуказанные не трогаются, `null` — явный сброс.
- Перечисленные `children` синхронизируются по имени (создаются при отсутствии), лишние дети НЕ удаляются. Удаление — только `{ "action": "delete", "path": "..." }`.
- Узел: `active`, `index` (порядок среди сиблингов), `rect` (`anchorMin`/`anchorMax`/`pivot`/`pos`/`size`/`rotation`/`scale`) ИЛИ `stretch` (`left`/`right`/`top`/`bottom`), `prefab` (только для нового узла — инстанс вложенного префаба, `children` — оверрайды внутри), `components`, `children`.

## Компоненты

- `{ "type": "Image", "sprite": "путь" | "путь#SubSprite" | null, "color": "#RRGGBB[AA]", "imageType": "Simple|Sliced|Tiled|Filled", "raycast": false, "fillCenter": true, "ppuMultiplier": 1 }`
- `{ "type": "Text", "text": "...", "size": 42, "color": "#...", "align": "Center", "font": "путь до TMP_FontAsset", "wrap": false }` — TMP; без `font` — дефолт TMP Settings, но в проекте обычно есть свой шрифт в UNITYAGENT-UI.md.
- `{ "type": "Button", "targetGraphic": "#Image", "wire": [ { "target": "", "type": "UI.MyModal", "method": "OnCloseClicked" } ] }` — `wire` полностью замещает persistent-листенеры onClick.
- Любой другой компонент — по имени типа: `{ "type": "UI.MyView", "set": { "Speed": 2, "Mode": "Fast" }, "ref": { "Icon": "Icon#Image", "Data": "asset:Assets/Configs/X.asset" } }`. `set` — значения (число/строка/bool/`[x,y]`/`[x,y,z]`/`"#цвет"`/имя enum), `ref` — ссылки: `"путь"` → GameObject, `"путь#Тип"` → компонент, `""` → корень, `"asset:путь"` → ассет.
- `set`/`ref` работают и на нативных типах для остальных свойств.
- Компонент матчится по типу: существующий обновляется, отсутствующий добавляется.

## dump — чтение экрана

`{ "action": "dump" }` → `Library/AgentBridge/Artifacts/<TaskId>/uidump.json`: всё дерево с анкорами, размерами и `screenRect` `[x, y, w, h]` в референсных пикселях (начало — левый верхний угол). Читай его вместо YAML префаба — там же object-ссылки кастомных компонентов.

## shot — скриншот

`{ "action": "shot", "output": "имя.png", "width": 1920, "height": 1080, "outline": ["Popup"] }` — всё опционально. Результат всегда пишется в `Library/AgentBridge/Artifacts/<TaskId>/`; без `output` имя равно `shot.png`. `output` — только имя PNG-файла, не путь: абсолютные пути и сегменты каталогов запрещены. Рядом всегда `<output>.rects.json` с экранными ректами всех узлов; `outline` рисует цветные рамки по путям (легенда в rects.json). Смотри PNG глазами, координаты сверяй по rects.json.

## Правила

- Перед вёрсткой в новом проекте прочитай `UNITYAGENT-UI.md` (рекурсивный поиск от корня проекта, или устаревшее `UNITYCOWORK-UI.md`) — референсное разрешение, палитра, шрифты, пути к арту и префабам. Нет файла — спроси пользователя про соглашения.
- Один префаб — одна задача. Несколько префабов — несколько задач подряд.
- Всегда завершай итерацию правки скриншотом (`apply` + `shot` в одной задаче).
- Не пиши `shot` в корень проекта, `Assets`, `Docs` или абсолютный путь: мост принимает только имя файла и сам помещает его в каталог артефактов задачи.
- Кастомные view-компоненты и их ссылки заполняй через `set`/`ref` — не оставляй пустых object-полей у собранных экранов.
- Логика (обработчики, анимации, код) — не сюда: код пишется обычным путём, вёрстка только собирает префаб и ссылки.
- Файлы тасков и `Artifacts/` не удаляй — очистка принадлежит мосту (авто-трим).

# ТДД: Декларативные UI-задачи (агентская вёрстка uGUI)

Status: Выполнено

## Отклонения от ТДД при реализации

- **Dump-стейдж — камера вместо overlay.** ТДД предполагал для `dump` `ScreenSpaceOverlay`-canvas без камеры и вычисление `screenRect` «с учётом scaleFactor». Это дало бы координаты, зависящие от размера Game View редактора, что ломает контракт «референсные пиксели». Реализация использует единый `UiPrefabStage` с камерой + `RenderTexture` заданного разрешения в обоих режимах (dump и shot): пиксельные размеры канваса фиксированы референсным разрешением независимо от Game View, а `ScreenRect` считается через `RectTransformUtility.WorldToScreenPoint(camera, corner)` детерминированно. SRP отключается и слой 31 изолируется только в режиме рендера (`shot`).
- **Очистка результатов — свип «сирот».** По просьбе заказчика заодно закрыта давняя проблема: `testresult_*` (а теперь и `uidump_*`/`shot_*`) могли оставаться в папке, если файл результата записывался уже после того, как исходный таск был вычищен (тримом/`clean.command`) — «осиротевший» результат больше никогда не попадал под удаление, т.к. очистка перебирает файлы задач. Добавлен `TaskCleaner.SweepOrphans`: на простое и во всех ручных очистках удаляются любые `result_*`/`testresult_*`/`pending_errors_*`/`uidump_*`/`shot_*(.rects.json)`, у которых нет живого файла-задачи (`.cs`/`.ui.json`). Кастомные пути `output` не трогаются.

## Цель

Второй тип задач Cowork Bridge — декларативные `Task_*.ui.json`: правка и создание uGUI-префабов, дамп геометрии и скриншоты без компиляции C# и domain reload. Новый модуль `Ui` в пакете и новый скилл `unity-ui` в плагине. Охват — только uGUI + TMP, UI Toolkit не поддерживается.

## Состав изменений

- Новые файлы пакета: `CoworkBridge/Editor/Ui/UiJson.cs`, `UiPath.cs`, `UiValue.cs`, `UiComponentSync.cs`, `UiNodeApplier.cs`, `UiPrefabStage.cs`, `UiStage.cs`, `UiDumper.cs`, `UiScreenshot.cs`, `UiTaskRunner.cs`
- Правки пакета: `CoworkBridge/Editor/CoworkBridge.cs`, `CoworkBridge/Editor/TaskCleaner.cs`
- Новый скилл: `unity-bridge-plugin/skills/unity-ui/SKILL.md`
- Правка скилла: `unity-bridge-plugin/skills/unity-bridge/SKILL.md`
- Версии: `CoworkBridge/package.json` → `0.4.0`, `unity-bridge-plugin/.claude-plugin/plugin.json` → `1.1.0`, пересобрать `unity-bridge-plugin.zip`

## Формат задачи

Файл `Assets/Editor/CoworkBridge/Task_YYYYMMDD_HHMMSS.ui.json`. Идентификатор задачи — имя файла без суффикса `.ui.json` (`Task_20260710_113000.ui.json` → `Task_20260710_113000`). Результат — стандартные `result_<TaskId>.json` + `result_<TaskId>.done`.

```json
{
	"prefab": "Assets/Resources/Prefabs/UI/MapScreenModal.prefab",
	"actions": [
		{ "action": "apply", "target": "Popup", "node": { } },
		{ "action": "delete", "path": "Popup/OldTitle" },
		{ "action": "dump" },
		{ "action": "shot", "output": "shot_Task_20260710_113000.png", "width": 1920, "height": 1080, "outline": ["Popup"] }
	]
}
```

- `prefab` — один префаб на задачу. Если файла нет и в actions есть `apply` — префаб создаётся: корень `RectTransform` stretch 0..1, имя — имя файла без расширения.
- Порядок исполнения: сначала все `apply`/`delete` в порядке перечисления над загруженным содержимым префаба, затем одно сохранение, затем `dump`/`shot` в порядке перечисления над сохранённым ассетом.
- Любая ошибка (битый JSON, не найден префаб/спрайт/тип/путь) → `status: "runtime_error"`, сообщение в `logs`, префаб не сохраняется, содержимое выгружается в `finally`.

## Действие apply

- `target` — путь до узла от корня (`"Popup/Title"`); `""` — сам корень префаба. Отсутствующие сегменты пути создаются как пустые `RectTransform`.
- `node` — фрагмент. Имя узла берётся из последнего сегмента `target`, поле `name` внутри `node` не используется (только у `children`).

Семантика синхронизации фрагмента:

- указанные свойства ставятся, неуказанные не трогаются
- значение `null` у свойства — явный сброс (спрайт → null, ссылка → null)
- перечисленные `children` синхронизируются по имени: существующий — обновляется, отсутствующий — создаётся
- лишние дети НЕ удаляются; удаление — только действием `delete`

Формат узла:

```json
{
	"active": true,
	"index": 0,
	"rect": { "anchorMin": [0.5, 0.5], "anchorMax": [0.5, 0.5], "pivot": [0.5, 0.5], "pos": [0, 120], "size": [400, 60], "rotation": 0, "scale": [1, 1] },
	"stretch": { "left": 0, "right": 0, "top": 0, "bottom": 0 },
	"prefab": "Assets/Resources/Prefabs/UI/Parts/SkillNode.prefab",
	"components": [ ],
	"children": [ { "name": "Title" } ]
}
```

- `rect` и `stretch` взаимоисключающие; `stretch` = анкоры 0..1 + отступы. Внутри `rect` все поля опциональны.
- `index` — `SetSiblingIndex`.
- `prefab` допустим только для создаваемого узла: инстанс через `PrefabUtility.InstantiatePrefab` с переименованием в имя узла. Для существующего узла с `prefab` — ошибка. `children` у префаб-узла — оверрайды по именам внутри инстанса.
- Переименование узлов не поддерживается: идентичность — путь.

## Компоненты

Массив `components`. Матчинг: по типу — существующий компонент этого типа обновляется, отсутствующий добавляется. Тип ищется как алиас (`Image`, `Text`, `Button`), иначе `TaskRunner.FindType` (короткое или полное имя, все сборки).

Нативные алиасы:

```json
{ "type": "Image", "sprite": "Assets/Sprites/UI/Node.png", "color": "#FF005A", "imageType": "Sliced", "raycast": false, "fillCenter": true, "ppuMultiplier": 1 }
{ "type": "Text", "text": "МИССИИ", "size": 42, "color": "#FF005A", "align": "Center", "font": "Assets/TextMesh Pro/.../Strogo-Regular SDF.asset", "wrap": false }
{ "type": "Button", "targetGraphic": "#Image", "wire": [ { "target": "", "type": "UI.MapScreenModal", "method": "OnCloseClicked" } ] }
```

- `Text` — `TextMeshProUGUI`, `raycastTarget` = false, `font` не указан → `TMP_Settings.defaultFontAsset`. `align` — имя значения `TextAlignmentOptions`.
- `Image.imageType` — имя значения `Image.Type`. `sprite` поддерживает суб-ассет: `"Assets/atlas.png#SpriteName"` (через `LoadAllAssetsAtPath` + матч по имени).
- `Button` — `Transition.None`. `wire` полностью замещает persistent-листенеры `onClick`: очистить все, добавить перечисленные (`UnityEventTools.AddVoidPersistentListener`; `target` — путь до узла с компонентом `type`).
- У любого компонента (включая нативные) дополнительно допустимы `set` и `ref`.

Универсальный компонент:

```json
{ "type": "UI.MapMissionView", "set": { "FloorFormat": "Этаж {0}" }, "ref": { "Icon": "Icon#Image", "Popup": "Popup", "NodeSprite": "asset:Assets/Sprites/UI/Node.png" } }
```

- `set` — значения через `SerializedObject`: поиск свойства по точному имени, затем по `"m_" + имя`; поддерживаются вложенные пути SerializedProperty через точку. Типы значений: bool, число, строка, `[x,y]`, `[x,y,z]`, строка `"#RRGGBB"`/`"#RRGGBBAA"` → Color, имя enum-значения (матч по `enumNames` без регистра, число — как индекс).
- `ref` — object-ссылки: `"путь"` → GameObject узла; `"путь#Тип"` → компонент на узле; `""` → корень; `"asset:путь"` → ассет (`LoadAssetAtPath<Object>`, для `#` — суб-ассет).

## Действие delete

`{ "action": "delete", "path": "Popup/OldTitle" }` — `DestroyImmediate` узла. Узла нет — предупреждение в лог, не ошибка.

## Действие dump

`{ "action": "dump" }` → файл `uidump_<TaskId>.json` в папке задач. Инстанцирование префаба во временной сцене под canvas 1920×1080 (см. UiPrefabStage) без рендера, обход дерева:

```json
{
	"prefab": "Assets/Resources/Prefabs/UI/MapScreenModal.prefab",
	"reference": [1920, 1080],
	"root": {
		"name": "MapScreenModal",
		"path": "",
		"active": true,
		"screenRect": [0, 0, 1920, 1080],
		"rect": { "anchorMin": [0, 0], "anchorMax": [1, 1], "pivot": [0.5, 0.5], "pos": [0, 0], "size": [0, 0] },
		"prefab": "Assets/...(только у корней вложенных инстансов)",
		"components": [
			{ "type": "Image", "sprite": "Assets/...", "color": "#0D0406", "imageType": "Simple", "raycast": true },
			{ "type": "MapScreenModal", "refs": { "FloorText": "Header/Floor#TextMeshProUGUI", "LayerContainer": "Scroll/Content" } }
		],
		"children": [ ]
	}
}
```

- `screenRect` — `[x, y, w, h]` в референсных пикселях, начало координат — левый верхний угол. Вычисление: `rt.GetWorldCorners` → `camera.WorldToScreenPoint` → `y_img = height - y_screen` для верхней грани.
- Нативные компоненты — ключевые свойства как в формате apply. Прочие компоненты — имя типа + `refs`: все object-reference свойства верхнего уровня SerializedObject, значения в синтаксисе ref (путь внутри префаба, `asset:путь`, либо `"~external"` для ссылок наружу).
- Неактивные узлы включаются (`active: false`), `screenRect` для них вычисляется по RectTransform как есть.

## Действие shot

`{ "action": "shot", "output": "shot_<TaskId>.png", "width": 1920, "height": 1080, "outline": ["Popup"] }`

- Все поля опциональны: `output` по умолчанию `shot_<TaskId>.png` (пишется в папку задач; относительный путь — от корня Unity-проекта), размеры по умолчанию 1920×1080, `outline` по умолчанию пуст.
- Рендер — порт `UiBuild.Screenshot` из Deadlift (код ниже в UiScreenshot): временная сцена, ScreenSpaceCamera-canvas, отключение пользовательских MonoBehaviour, изоляция слоем 31, временное отключение SRP-пайплайна, RenderTexture → PNG.
- Рядом всегда пишется `<output>.rects.json`: плоский список всех узлов с `path` и `screenRect` (формула как в dump) плюс легенда обводок.
- `outline` — список путей: в PNG поверх рисуется рамка толщиной 2px цветом из фиксированной палитры по порядку (`#FF3B30`, `#34C759`, `#0A84FF`, `#FFD60A`, `#BF5AF2`, `#FF9F0A`, `#64D2FF`, `#FF375F`; при исчерпании — по кругу). При записи пикселей в Texture2D учесть инверсию Y.

```json
{
	"reference": [1920, 1080],
	"nodes": [ { "path": "Popup/Title", "rect": [760, 420, 400, 60] } ],
	"outlines": [ { "path": "Popup", "color": "#FF3B30" } ]
}
```

## Файлы модуля Ui

Все классы — `namespace CoworkBridge.Ui`, сборка `CoworkBridge`. В `CoworkBridge.asmdef` в `references` добавить `UnityEngine.UI` и `Unity.TextMeshPro`.

### UiJson.cs

Динамический JSON без зависимостей (`JsonUtility` не подходит). Полный код:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoworkBridge.Ui
{
	public static class UiJson
	{
		public static object Parse(string json)
		{
			int pos = 0;
			object value = ParseValue(json, ref pos);
			SkipWhitespace(json, ref pos);
			if (pos != json.Length)
				throw new FormatException("Unexpected trailing characters at " + pos);
			return value;
		}

		private static object ParseValue(string s, ref int pos)
		{
			SkipWhitespace(s, ref pos);
			if (pos >= s.Length)
				throw new FormatException("Unexpected end of JSON");
			char c = s[pos];
			if (c == '{')
				return ParseObject(s, ref pos);
			if (c == '[')
				return ParseArray(s, ref pos);
			if (c == '"')
				return ParseString(s, ref pos);
			if (c == 't')
				return ParseLiteral(s, ref pos, "true", true);
			if (c == 'f')
				return ParseLiteral(s, ref pos, "false", false);
			if (c == 'n')
				return ParseLiteral(s, ref pos, "null", null);
			return ParseNumber(s, ref pos);
		}

		private static Dictionary<string, object> ParseObject(string s, ref int pos)
		{
			var result = new Dictionary<string, object>();
			pos++;
			SkipWhitespace(s, ref pos);
			if (s[pos] == '}')
			{
				pos++;
				return result;
			}
			while (true)
			{
				SkipWhitespace(s, ref pos);
				string key = ParseString(s, ref pos);
				SkipWhitespace(s, ref pos);
				if (s[pos] != ':')
					throw new FormatException("Expected ':' at " + pos);
				pos++;
				result[key] = ParseValue(s, ref pos);
				SkipWhitespace(s, ref pos);
				if (s[pos] == ',')
				{
					pos++;
					continue;
				}
				if (s[pos] == '}')
				{
					pos++;
					return result;
				}
				throw new FormatException("Expected ',' or '}' at " + pos);
			}
		}

		private static List<object> ParseArray(string s, ref int pos)
		{
			var result = new List<object>();
			pos++;
			SkipWhitespace(s, ref pos);
			if (s[pos] == ']')
			{
				pos++;
				return result;
			}
			while (true)
			{
				result.Add(ParseValue(s, ref pos));
				SkipWhitespace(s, ref pos);
				if (s[pos] == ',')
				{
					pos++;
					continue;
				}
				if (s[pos] == ']')
				{
					pos++;
					return result;
				}
				throw new FormatException("Expected ',' or ']' at " + pos);
			}
		}

		private static string ParseString(string s, ref int pos)
		{
			if (s[pos] != '"')
				throw new FormatException("Expected string at " + pos);
			pos++;
			var sb = new StringBuilder();
			while (true)
			{
				if (pos >= s.Length)
					throw new FormatException("Unterminated string");
				char c = s[pos++];
				if (c == '"')
					return sb.ToString();
				if (c == '\\')
				{
					char e = s[pos++];
					switch (e)
					{
						case '"': sb.Append('"'); break;
						case '\\': sb.Append('\\'); break;
						case '/': sb.Append('/'); break;
						case 'b': sb.Append('\b'); break;
						case 'f': sb.Append('\f'); break;
						case 'n': sb.Append('\n'); break;
						case 'r': sb.Append('\r'); break;
						case 't': sb.Append('\t'); break;
						case 'u':
							sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
							pos += 4;
							break;
						default: throw new FormatException("Bad escape \\" + e);
					}
				}
				else
				{
					sb.Append(c);
				}
			}
		}

		private static object ParseLiteral(string s, ref int pos, string literal, object value)
		{
			if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
				throw new FormatException("Bad literal at " + pos);
			pos += literal.Length;
			return value;
		}

		private static object ParseNumber(string s, ref int pos)
		{
			int start = pos;
			while (pos < s.Length && ("+-0123456789.eE".IndexOf(s[pos]) >= 0))
				pos++;
			string token = s.Substring(start, pos - start);
			if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
				throw new FormatException("Bad number '" + token + "' at " + start);
			return result;
		}

		private static void SkipWhitespace(string s, ref int pos)
		{
			while (pos < s.Length && char.IsWhiteSpace(s[pos]))
				pos++;
		}

		public static string Write(object value, bool pretty = true)
		{
			var sb = new StringBuilder();
			WriteValue(sb, value, pretty, 0);
			return sb.ToString();
		}

		private static void WriteValue(StringBuilder sb, object value, bool pretty, int depth)
		{
			if (value == null)
			{
				sb.Append("null");
				return;
			}
			if (value is string str)
			{
				WriteString(sb, str);
				return;
			}
			if (value is bool b)
			{
				sb.Append(b ? "true" : "false");
				return;
			}
			if (value is IDictionary dict)
			{
				WriteObject(sb, dict, pretty, depth);
				return;
			}
			if (value is IList list)
			{
				WriteArray(sb, list, pretty, depth);
				return;
			}
			sb.Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
		}

		private static void WriteObject(StringBuilder sb, IDictionary dict, bool pretty, int depth)
		{
			sb.Append('{');
			bool first = true;
			foreach (DictionaryEntry entry in dict)
			{
				if (!first)
					sb.Append(',');
				first = false;
				NewLine(sb, pretty, depth + 1);
				WriteString(sb, (string)entry.Key);
				sb.Append(pretty ? ": " : ":");
				WriteValue(sb, entry.Value, pretty, depth + 1);
			}
			if (!first)
				NewLine(sb, pretty, depth);
			sb.Append('}');
		}

		private static void WriteArray(StringBuilder sb, IList list, bool pretty, int depth)
		{
			sb.Append('[');
			bool first = true;
			foreach (object item in list)
			{
				if (!first)
					sb.Append(pretty ? ", " : ",");
				first = false;
				WriteValue(sb, item, pretty, depth + 1);
			}
			sb.Append(']');
		}

		private static void WriteString(StringBuilder sb, string s)
		{
			sb.Append('"');
			foreach (char c in s)
			{
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\b': sb.Append("\\b"); break;
					case '\f': sb.Append("\\f"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < ' ')
							sb.Append("\\u").Append(((int)c).ToString("x4"));
						else
							sb.Append(c);
						break;
				}
			}
			sb.Append('"');
		}

		private static void NewLine(StringBuilder sb, bool pretty, int depth)
		{
			if (!pretty)
				return;
			sb.Append('\n');
			for (int i = 0; i < depth; i++)
				sb.Append('\t');
		}
	}
}
```

### UiPath.cs

- `static Transform Resolve(Transform root, string path)` — путь по именам с поддержкой индекса дублей `Name[2]` (второе вхождение имени среди детей, счёт с 0 — `Name` эквивалентно `Name[0]`). Не найдено → null.
- `static Transform ResolveOrCreate(Transform root, string path, List<string> log)` — как Resolve, но отсутствующие сегменты создаются (`new GameObject(name, typeof(RectTransform))`, `SetParent(parent, false)`); индексная форма для отсутствующего сегмента → исключение.
- `static string PathOf(Transform t, Transform root)` — обратная операция для dump/rects: сегменты имён от корня, при дублях имени среди сиблингов — с индексом.

### UiValue.cs

Конвертация JSON-значений:

- `static Color Hex(string hex)` — `ColorUtility.TryParseHtmlString`, ошибка → исключение.
- `static Vector2 V2(object value)`, `static Vector3 V3(object value)` — из `List<object>`.
- `static float F(object value)`, `static int I(object value)`, `static bool B(object value)`.
- `static UnityEngine.Object Sprite(string path)` — `"путь"` или `"путь#SubAsset"`; суб-ассет через `AssetDatabase.LoadAllAssetsAtPath` + матч по имени и типу `Sprite`; не найдено → исключение.
- `static void SetProperty(SerializedObject so, string name, object value)` — `FindProperty(name)`, затем `FindProperty("m_" + name)`; switch по `propertyType`: Float/Integer/Boolean/String/Color (строка `#...`)/Vector2/Vector3/Enum (строка → индекс в `enumNames` без учёта регистра, число → индекс); неподдержанный тип → исключение. `ApplyModifiedPropertiesWithoutUndo` вызывает вызывающая сторона один раз после всех set.
- `static object RefValue(GameObject root, string spec)` — синтаксис ref: `""` → корень (GameObject); `"asset:путь"` → `LoadAssetAtPath<Object>` (с `#` — суб-ассет); `"путь"` → GameObject; `"путь#Тип"` → компонент по короткому/полному имени типа. Не найдено → исключение.

### UiComponentSync.cs

`static void Sync(GameObject go, Dictionary<string, object> comp, GameObject root, List<string> log)`:

- определить тип: алиасы `Image` → `UnityEngine.UI.Image`, `Text` → `TMPro.TextMeshProUGUI`, `Button` → `UnityEngine.UI.Button`; иначе `TaskRunner.FindType(typeName)`, null → исключение
- найти компонент этого типа на go (`GetComponent(type)`), нет → `AddComponent(type)`; для `Text` при добавлении — `raycastTarget = false`; для `Button` при добавлении — `transition = None`
- применить нативные поля (какие указаны): Image — `sprite` (строка/null), `color`, `imageType`, `raycast`, `fillCenter`, `ppuMultiplier`; Text — `text`, `size` (`fontSize`), `color`, `align`, `font`, `wrap` (`enableWordWrapping`); Button — `targetGraphic` (ref-синтаксис относительно узла компонента: `"#Image"` — на этом же узле), `wire`
- `wire`: `UnityEventTools.RemovePersistentListener` циклом до нуля (`onClick.GetPersistentEventCount`), затем для каждого элемента: resolve узла `target`, `GetComponent` по `type` (через FindType), `Delegate.CreateDelegate(typeof(UnityAction), comp, method)`, `AddVoidPersistentListener`
- применить `set` и `ref` через один `SerializedObject` (ref — `objectReferenceValue` c `UiValue.RefValue(root, spec)`), затем `ApplyModifiedPropertiesWithoutUndo`

### UiNodeApplier.cs

- `static void Apply(GameObject root, string target, Dictionary<string, object> node, List<string> log)` — resolve/create целевого узла: если `target == ""` → корень; если узла нет и в node есть `prefab` — создать родительскую цепочку, инстанцировать префаб (`InstantiatePrefab` + `SetParent(parent, false)` + переименовать в последний сегмент); иначе `ResolveOrCreate`; затем `SyncNode`.
- `static void SyncNode(GameObject go, Dictionary<string, object> node, GameObject root, List<string> log)`:
  - `active` → `SetActive`
  - `stretch` → анкоры (0,0)-(1,1), `offsetMin = (left, bottom)`, `offsetMax = (-right, -top)`
  - `rect` → по указанным полям: `anchorMin`, `anchorMax`, `pivot`, `pos` (`anchoredPosition`), `size` (`sizeDelta`), `rotation` (Z-градусы, `localEulerAngles`), `scale` (`localScale`, z=1)
  - `index` → `SetSiblingIndex`
  - `components` → `UiComponentSync.Sync` для каждого
  - `children` → для каждого элемента по `name`: поиск прямого ребёнка; нет — создание (пустой RectTransform или инстанс по `prefab` элемента); рекурсивно `SyncNode`
  - `prefab` на существующем узле, не являющемся корнем инстанса этого же префаба → исключение
- `static void Delete(GameObject root, string path, List<string> log)` — resolve; есть → `DestroyImmediate(go)`, нет → warning в log.

### UiPrefabStage.cs

Общий каркас для dump и shot:

- `static UiStage Open(string prefabPath, int width, int height, bool render)` — загрузка префаба-ассета; новая additive-пустая сцена; при `render == true` — отключение SRP (сохранить `QualitySettings.renderPipeline`/`GraphicsSettings.defaultRenderPipeline`, поставить null) и камера (`orthographic`, SolidColor `#0D0D0D`, `cullingMask` слой 31); canvas `ScreenSpaceCamera` (при `render == false` — `ScreenSpaceOverlay` без камеры), `CanvasScaler.ScaleWithScreenSize` с `referenceResolution = (width, height)`; инстанс префаба под canvas; отключение всех MonoBehaviour, чей namespace не начинается с `UnityEngine`/`TMPro`; `CanvasGroup.alpha = 1` на корне при наличии; слой 31 рекурсивно при render; `TMP_Text.ForceMeshUpdate(true, true)` по всем; `Canvas.ForceUpdateCanvases()`.
- `UiStage` — тип в отдельном файле `UiStage.cs`: поля `Scene Scene`, `Camera Camera`, `Canvas Canvas`, `GameObject Instance`, `int Width`, `int Height` и сохранённые пайплайны.
- `static void Close(UiStage stage)` — закрыть сцену, восстановить пайплайны.
- `static UnityEngine.Rect ScreenRect(UiStage stage, RectTransform rt)` — `GetWorldCorners`; при наличии камеры — `WorldToScreenPoint`, иначе corners уже в экранных координатах overlay-canvas с учётом `scaleFactor`; вернуть `[xMin, height - yMax, w, h]` (начало — левый верхний угол).

### UiDumper.cs

`static string Dump(string prefabPath, string outputPath)`:

- `Open(prefabPath, 1920, 1080, render: false)`
- рекурсивный обход инстанса: узел → Dictionary по формату dump; компоненты: Image/TMP/Button — нативные ключи (у Button — `targetGraphic` как путь-ref и список persistent-листенеров `{target, type, method}`), RectTransform/Canvas-служебные пропускаются, прочие — `type` + `refs` (обход итератором SerializedObject по верхнему уровню, только `propertyType == ObjectReference`, значение → путь внутри инстанса через `PathOf`, ассет → `asset:путь` через `AssetDatabase.GetAssetPath`, иначе `"~external"`); признак корня вложенного инстанса — `PrefabUtility.IsAnyPrefabInstanceRoot(go)` → поле `prefab` с `GetPrefabAssetPathOfNearestInstanceRoot`
- `Close`, сериализация `UiJson.Write`, запись в `outputPath`, вернуть путь

### UiScreenshot.cs

`static string Shot(string prefabPath, string outputPng, int width, int height, List<string> outlinePaths)`:

- `Open(prefabPath, width, height, render: true)`
- RenderTexture ARGB32, `cam.targetTexture`, двойной `cam.Render()`, `ReadPixels` в Texture2D — как в исходном `UiBuild.Screenshot` (Deadlift `Assets/Editor/UiBuilder/UiBuild.cs`, метод переносится с заменой каркаса на UiPrefabStage)
- собрать список всех RectTransform инстанса: `path` + `ScreenRect` → `<outputPng>.rects.json` (формат выше)
- для каждого `outlinePaths[i]`: resolve узла, рект → рамка 2px цветом палитры `i % 8` прямо в Texture2D (`SetPixel`, координата в текстуре `y_tex = height - 1 - y_img`), клэмп по границам
- `EncodeToPNG`, запись, `Close`, вернуть путь PNG

### UiTaskRunner.cs

`static void Execute(string taskId, string coworkPath)`:

- прочитать `<coworkPath>/<taskId>.ui.json`, `UiJson.Parse`; логи собирать в `List<string>` (плюс перехват `Application.logMessageReceived` как в TaskRunner)
- разобрать `prefab` и `actions`; мутации (`apply`/`delete`) выполнить над `PrefabUtility.LoadPrefabContents` (или новым корнем при отсутствии файла), `SaveAsPrefabAsset` + `UnloadPrefabContents` один раз; затем `dump`/`shot`
- пути вывода: относительные — от корня Unity-проекта; `dump` всегда в `<coworkPath>/uidump_<taskId>.json`; `shot` по умолчанию `<coworkPath>/shot_<taskId>.png`
- `AssetDatabase.Refresh()` после записи PNG
- успех → `TaskResult { id, status = "success", logs, return_value }`, `return_value` — перечисление сделанного и путей выходных файлов; исключение → `status = "runtime_error"`, message + stack в logs, `UnloadPrefabContents` в finally, префаб не сохранять
- запись через `ResultWriter.Write`

## Правки CoworkBridge.cs

- `FindNextTask()` — сканировать `*.cs` и `*.ui.json`; taskId для `.ui.json` — имя файла с усечением суффикса `.ui.json` (не `GetFileNameWithoutExtension`, он оставит `.ui`); общая сортировка по `GetCreationTimeUtc`; фильтр по отсутствию `result_<taskId>.done` как сейчас.
- В `OnEditorUpdate()` после получения следующего таска: если файл оканчивается на `.ui.json` — `CleanResultFiles(taskId)` и `Ui.UiTaskRunner.Execute(taskId, _coworkPath)` немедленно, без `PendingTaskKey`, без `RequestScriptCompilation`. Ветка `.cs` — без изменений.
- `RunTaskManual()` — фильтр диалога оставить `cs` (ручной запуск ui-задач не нужен: положенный файл подхватится сканом).

## Правки TaskCleaner.cs

- Ввести `private static List<string> GetTaskFiles(string coworkPath)` — объединение `*.cs` и `*.ui.json` с корректным вычислением taskId; использовать во всех методах вместо прямых `GetFiles(coworkPath, "*.cs")` (`TrimCompleted` — сравнение с keepCount по числу задач, `CleanCompleted`, `CleanAll`, `GetSuccessfulTaskIds`).
- `DeleteTaskFiles` — дополнительно удалять: `<taskId>.ui.json`, `uidump_<taskId>.json`, `shot_<taskId>.png` (+ `.meta`), `shot_<taskId>.png.rects.json`. Кастомные пути `output` клинер не трогает.

## Скилл unity-ui

Новый файл `unity-bridge-plugin/skills/unity-ui/SKILL.md`, содержимое:

````markdown
---
name: unity-ui
description: "Use this skill for any uGUI layout work in a Unity project via Cowork Bridge — creating or editing UI prefabs (RectTransforms, Images, TMP texts, Buttons, custom view components), inspecting layout geometry, and screenshotting UI prefabs. Works through declarative Task_*.ui.json tasks: no C# compilation, no domain reload, iterations take seconds. Trigger on any layout phrasing: 'сверстай экран/попап', 'поправь вёрстку', 'подвинь/выровняй/перекрась элемент', 'создай UI-префаб', 'скриншот экрана', 'что где стоит на экране'. Do NOT use for gameplay logic, non-UI editor operations (use unity-bridge), or UI Toolkit (not supported — uGUI + TMP only)."
---

# Unity UI — декларативная вёрстка uGUI

Вёрстка через декларативные задачи Cowork Bridge: кладёшь `Task_YYYYMMDD_HHMMSS.ui.json` в `Assets/Editor/CoworkBridge/`, Bridge применяет его к префабу без компиляции и возвращает результат.

## Quick reference

| Шаг | Действие |
|-----|----------|
| 1 | Найти `UNITYCOWORK-UI.md` в проекте — соглашения вёрстки (палитра, шрифты, арт, пути префабов) |
| 2 | Незнакомый префаб — сначала задача с `dump`, изучить `uidump_*.json` |
| 3 | Итерация: задача `[apply..., shot]` — правка и скриншот за один заход |
| 4 | Ждать результат: `bash Assets/Editor/CoworkBridge/wait-for-result.sh <TaskId> 300` |
| 5 | Посмотреть скриншот и `rects.json`, продолжить итерации |

Имя задачи: `Task_YYYYMMDD_HHMMSS`, файл `<TaskId>.ui.json`. Результат — `result_<TaskId>.json` + `.done`, статусы `success`/`runtime_error`/`timeout`. Очистка — как в unity-bridge (`clean.command`, авто-трим).

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
- `{ "type": "Text", "text": "...", "size": 42, "color": "#...", "align": "Center", "font": "путь до TMP_FontAsset", "wrap": false }` — TMP; без `font` — дефолт TMP Settings, но в проекте обычно есть свой шрифт в UNITYCOWORK-UI.md.
- `{ "type": "Button", "targetGraphic": "#Image", "wire": [ { "target": "", "type": "UI.MyModal", "method": "OnCloseClicked" } ] }` — `wire` полностью замещает persistent-листенеры onClick.
- Любой другой компонент — по имени типа: `{ "type": "UI.MyView", "set": { "Speed": 2, "Mode": "Fast" }, "ref": { "Icon": "Icon#Image", "Data": "asset:Assets/Configs/X.asset" } }`. `set` — значения (число/строка/bool/`[x,y]`/`[x,y,z]`/`"#цвет"`/имя enum), `ref` — ссылки: `"путь"` → GameObject, `"путь#Тип"` → компонент, `""` → корень, `"asset:путь"` → ассет.
- `set`/`ref` работают и на нативных типах для остальных свойств.
- Компонент матчится по типу: существующий обновляется, отсутствующий добавляется.

## dump — чтение экрана

`{ "action": "dump" }` → `uidump_<TaskId>.json` в папке задач: всё дерево с анкорами, размерами и `screenRect` `[x, y, w, h]` в референсных пикселях (начало — левый верхний угол). Читай его вместо YAML префаба — там же object-ссылки кастомных компонентов.

## shot — скриншот

`{ "action": "shot", "output": "имя.png", "width": 1920, "height": 1080, "outline": ["Popup"] }` — всё опционально. Рядом всегда `<output>.rects.json` с экранными ректами всех узлов; `outline` рисует цветные рамки по путям (легенда в rects.json). Смотри PNG глазами, координаты сверяй по rects.json.

## Правила

- Перед вёрсткой в новом проекте прочитай `UNITYCOWORK-UI.md` (рекурсивный поиск от корня проекта) — референсное разрешение, палитра, шрифты, пути к арту и префабам. Нет файла — спроси пользователя про соглашения.
- Один префаб — одна задача. Несколько префабов — несколько задач подряд.
- Всегда завершай итерацию правки скриншотом (`apply` + `shot` в одной задаче).
- Кастомные view-компоненты и их ссылки заполняй через `set`/`ref` — не оставляй пустых object-полей у собранных экранов.
- Логика (обработчики, анимации, код) — не сюда: код пишется обычным путём, вёрстка только собирает префаб и ссылки.
````

## Правка скилла unity-bridge

В `unity-bridge-plugin/skills/unity-bridge/SKILL.md` после вводного абзаца («Скилл для выполнения произвольных задач...») добавить строку:

```
Для вёрстки uGUI-префабов (создание/правка UI, скриншоты экранов) используй скилл `unity-ui` — декларативные задачи без компиляции. Этот скилл — для логики и всего остального.
```

В `description` фронтматтера добавить в конец: `For uGUI prefab layout and UI screenshots prefer the unity-ui skill.`

## Версии и сборка

- `CoworkBridge/package.json`: `"version": "0.4.0"`
- `unity-bridge-plugin/.claude-plugin/plugin.json`: `"version": "1.1.0"`
- Пересобрать `unity-bridge-plugin/unity-bridge-plugin.zip` (содержимое папки плагина, как собран текущий)

## Приёмка

- Задача с `apply` на несуществующий префаб создаёт префаб; повторный запуск той же задачи (после удаления `result_*`) не дублирует узлы и компоненты
- Задача `[apply, shot]` на существующем префабе выполняется без компиляции скриптов (в консоли нет compile/domain reload), время — секунды
- `dump` по `MapScreenModal`-подобному префабу отдаёт дерево со `screenRect` и object-ссылками кастомных компонентов
- `shot` с `outline` рисует рамки, `rects.json` совпадает с PNG по координатам
- `.cs`-задачи работают как раньше; очередь смешанных задач исполняется по времени создания
- `clean.command` и авто-трим удаляют ui-задачи вместе с `uidump_*`/`shot_*`
- Ошибка в JSON или отсутствующий спрайт → `runtime_error` с внятным сообщением, префаб не изменён

---

После выполнения:

- Поменяй статус в начале документа на `Выполнено`
- Уточни у заказчика, нужно ли обновить документацию проекта (README, UNITYCOWORK.md пакета) под изменения

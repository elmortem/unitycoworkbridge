# 261117-1000-TDD-input_digest_hash_cache — ход исполнения

## Единицы

- [x] `InputFileStamp`, `InputFileStamps`, `InputHashCache`, `InputFileHasher` с `.meta`, подключены в оба стенда
- [x] `ValidationInputSnapshot.Capture` через `InputFileHasher`, перегрузка с кэшем, `ReadBytes`, шапка
- [x] Кэш протянут через `InputHashJob` (+ `ReadBytes` в телеметрии), `BridgePaths`, `ValidationEvidence.HashCache`, `CachedResultServer`
- [x] C21 и EditMode `SnapshotDigestHashesEachFile` на новый формат, C27
- [x] Пакет 0.44.0, README/PROJECT_MAP/CLAUDE.md, `build-plugin.ps1`

## Решения

- ТДД переписан во время работы (вынесен `InputFileHasher`); реализация перестроена под новую редакцию.
- `Load()` при неразборчивой строке отбрасывает весь файл, а не только строку: порванный кэш стоит одного полного чтения.
- `Commit` пишет файл, только если записи реально изменились, — неизменный проект не переписывает кэш на каждом дайджесте.

## Решения за пользователя

- нет

## Упрощения

- Подготовительные дайджесты (`BeginPrepare`) по-прежнему идут через `Start()` без `Measure`, поэтому в телеметрии виден только `validation_complete` и `cache_lookup` — предел: не видно `ReadBytes` подготовки; пересмотреть, если понадобится разбор времени подготовки.

## Проверки

- Coordination `--group all` с `HashPlatform=UNITY_EDITOR_WIN`: PASS, C27 в перечне; без `HashPlatform`: PASS.
- Compile.Tests: 11/11.
- Unity compile: success; EditMode `AgentBridgeHashPerformanceTests` + `AgentBridgeEvidenceTests`: 19/19.
- Две `tests --fresh` подряд: `ReadBytes: 0` при `Bytes: 17385888`, `input-hash-cache.txt` существует.
- Правка временного `Assets/HashProbe.txt` (9 байт) между задачами: `ReadBytes: 9`; проба удалена.
- `build-plugin.ps1`: `version_check=PASS`, `zip_validation=PASS`.

## Замечания

- Появление нового файла рождает `.meta` от Unity посреди следующей задачи — честный `stale_input`, к кэшу не относится.

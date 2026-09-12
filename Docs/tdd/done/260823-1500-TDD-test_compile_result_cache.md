Status: Выполнено

# Кэш результатов tests/compile и коалесценция тестовых ранов

Проблема: несколько агентов в одном редакторе гоняют одни и те же тесты и компиляции по очереди, хотя между ранами проект не менялся. Мост получает кэш результатов, привязанный к отпечатку состояния проекта (fingerprint), и коалесценцию: повторный `tests`/`compile` при неизменном состоянии отвечается из кэша мгновенно, без lease и без смены сценового контекста; `tests`-таск, совместимый с уже идущим раном, присоединяется к нему и получает его результат. Изменение кода/ассетов меняет fingerprint и автоматически инвалидирует кэш, поэтому невалидные результаты раздать невозможно.

Правила поведения:

- Fingerprint тестов: `<pid>-<processStartTicks>-<AssetDatabase.GlobalArtifactDependencyVersion>`. Валиден только внутри одного процесса редактора.
- Fingerprint компиляции: SHA256 по метаданным (относительный путь, размер, mtime) всех `*.cs`, `*.asmdef`, `*.asmref`, `*.rsp` в `Assets/` и `Packages/`, плюс `ProjectSettings/ProjectSettings.asset`, `Packages/manifest.json`, `Packages/packages-lock.json`. Валиден и между перезапусками редактора.
- Fingerprint снимается на старте рана и сверяется на финализации; при несовпадении результат отдаётся только владельцу рана, в кэш не пишется, присоединённые таски возвращаются в очередь.
- Кэш одно-слотовый: последний завершённый ран на режим (`EditMode`/`PlayMode`) с пер-тестовым дампом; для compile — последний исход с диагностиками. Кэшируются и `test_failure`/`compiler_error` (те же исходники — те же ошибки). Прерванные/aborted раны не кэшируются.
- Cache hit обслуживается прямо из скана inbox: без lease, без сценового preflight, без `AgentSessionScheduler.OnTaskFinished`. Работает и пока другой таск активен, и во время play-сессии, и пока PlayMode-ран ждёт восстановления сцен.
- Ответ из кэша получает `Cached: true` и `SourceTaskId`. Флаг `--fresh` у `tests` и `compile` отключает кэш и коалесценцию (перезапуск флаки-тестов).
- Присоединение к идущему рану: только `tests`, тот же `TestMode`, текущий fingerprint равен стартовому fingerprint рана, фильтр запроса покрывается фильтром рана. Присоединённый таск получает журнальную запись со статусом `attached` (нетерминальный — CLI продолжает ждать) и обслуживается при финализации рана.
- Контракт для авторов тестов (фиксируется в SKILL.md): тесты контекстно-независимы — не зависят от открытых сцен, selection и прочего состояния редактора.

## References (not inlined)

- Конвенции кода: CLAUDE.md проекта (табы, каждый тип в отдельном файле, сериализуемые поля public с большой буквы).
- Координатор и хэши: `AgentBridgeUnity/Packages/com.elmortem.agentbridge/Editor/TaskCoordinator.cs`.
- Тестовый ран: `Editor/AgentTestRunner.cs`, `Editor/TestRunResult.cs`, `Editor/TestFailure.cs`.
- Компиляция: `Editor/CompileTaskExecutor.cs`.
- Журнал: `Editor/TaskJournal.cs`, `Editor/TaskRecord.cs`.
- Пути: `Editor/BridgePaths.cs`.
- CLI: `AgentBridgeCli/CliOptions.cs`, `AgentBridgeCli/AgentBridgeApplication.cs`, `AgentBridgeCli/BridgeClient.cs`, `AgentBridgeCli/TaskRequest.cs`, `AgentBridgeCli/TaskResultFormatter.cs`.
- Скилл: `unity-bridge-plugin/skills/unity-bridge/SKILL.md`.
- API: `AssetDatabase.GlobalArtifactDependencyVersion` — `public static uint`, Unity 2022.3.

## Новые файлы — Editor (Packages/com.elmortem.agentbridge/Editor)

Ко всем новым `.cs` Unity сгенерирует `.meta` при импорте.

### TestCaseResult.cs

```csharp
using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TestCaseResult
	{
		public string FullName;
		public string Assembly;
		public List<string> Categories = new List<string>();
		public string Status;
		public double DurationSeconds;
		public string Message;
		public string StackTrace;
	}
}
```

`Status` — строковое значение `TestStatus`: `Passed`, `Failed`, `Skipped`, `Inconclusive`.

### TestRunFilter.cs

```csharp
using System;

namespace AgentBridge
{
	[Serializable]
	public class TestRunFilter
	{
		public string TestMode;
		public string[] AssemblyNames = new string[0];
		public string[] TestNames = new string[0];
		public string[] CategoryNames = new string[0];
	}
}
```

### TestRunDump.cs

```csharp
using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TestRunDump
	{
		public int Version = 1;
		public string Fingerprint;
		public string SourceTaskId;
		public TestRunFilter Filter = new TestRunFilter();
		public string FinishedAtUtc;
		public List<TestCaseResult> Entries = new List<TestCaseResult>();
	}
}
```

### TestRunDumpStore.cs

```csharp
using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class TestRunDumpStore
	{
		public static void WritePending(TestRunDump dump)
		{
			WriteAtomic(PendingPath(dump.Filter.TestMode), JsonUtility.ToJson(dump));
		}

		public static bool TryTakePending(string testMode, out TestRunDump dump)
		{
			dump = Read(PendingPath(testMode));
			DeletePending(testMode);
			return dump != null;
		}

		public static void DeletePending(string testMode)
		{
			string path = PendingPath(testMode);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}

		public static void Write(TestRunDump dump)
		{
			WriteAtomic(FinalPath(dump.Filter.TestMode), JsonUtility.ToJson(dump));
		}

		public static bool TryRead(string testMode, out TestRunDump dump)
		{
			dump = Read(FinalPath(testMode));
			return dump != null;
		}

		private static TestRunDump Read(string path)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				return JsonUtility.FromJson<TestRunDump>(File.ReadAllText(path));
			}
			catch
			{
				return null;
			}
		}

		private static string FinalPath(string testMode)
		{
			return Path.Combine(BridgePaths.WorkingRoot, "test-cache-" + testMode.ToLowerInvariant() + ".json");
		}

		private static string PendingPath(string testMode)
		{
			return FinalPath(testMode) + ".pending";
		}

		private static void WriteAtomic(string path, string json)
		{
			string tempPath = path + ".tmp";
			File.WriteAllText(tempPath, json);

			if (File.Exists(path))
			{
				File.Replace(tempPath, path, null);
			}
			else
			{
				File.Move(tempPath, path);
			}
		}
	}
}
```

### TestFingerprint.cs

```csharp
using System.Diagnostics;
using UnityEditor;

namespace AgentBridge
{
	public static class TestFingerprint
	{
		private static readonly string ProcessStamp;

		static TestFingerprint()
		{
			Process process = Process.GetCurrentProcess();
			ProcessStamp = process.Id + "-" + process.StartTime.ToUniversalTime().Ticks;
		}

		public static string Current()
		{
			return ProcessStamp + "-" + AssetDatabase.GlobalArtifactDependencyVersion;
		}
	}
}
```

### CompileFingerprint.cs

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge
{
	public static class CompileFingerprint
	{
		private static readonly string[] Patterns = { "*.cs", "*.asmdef", "*.asmref", "*.rsp" };

		public static string Current()
		{
			string projectRoot = BridgePaths.ProjectRoot;
			var files = new List<string>();

			Collect(Path.Combine(projectRoot, "Assets"), files);
			Collect(Path.Combine(projectRoot, "Packages"), files);
			AddIfExists(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"), files);
			AddIfExists(Path.Combine(projectRoot, "Packages", "manifest.json"), files);
			AddIfExists(Path.Combine(projectRoot, "Packages", "packages-lock.json"), files);

			files.Sort(StringComparer.Ordinal);

			var builder = new StringBuilder();
			foreach (string file in files)
			{
				var info = new FileInfo(file);
				builder.Append(file.Substring(projectRoot.Length))
					.Append('|').Append(info.Length)
					.Append('|').Append(info.LastWriteTimeUtc.Ticks)
					.Append('\n');
			}

			using (SHA256 sha = SHA256.Create())
			{
				byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
				var hex = new StringBuilder(hash.Length * 2);
				foreach (byte b in hash)
				{
					hex.Append(b.ToString("x2"));
				}

				return hex.ToString();
			}
		}

		private static void Collect(string root, List<string> files)
		{
			if (!Directory.Exists(root))
			{
				return;
			}

			foreach (string pattern in Patterns)
			{
				files.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories));
			}
		}

		private static void AddIfExists(string path, List<string> files)
		{
			if (File.Exists(path))
			{
				files.Add(path);
			}
		}
	}
}
```

Изменения в `Library/PackageCache` не сканируются намеренно: immutable-пакеты меняются только через `manifest.json`/`packages-lock.json`, а они в отпечатке есть.

### CompileCacheEntry.cs

```csharp
using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class CompileCacheEntry
	{
		public int Version = 1;
		public string Fingerprint;
		public string SourceTaskId;
		public string Status;
		public List<TaskDiagnostic> Diagnostics = new List<TaskDiagnostic>();
		public string FinishedAtUtc;
	}
}
```

### CompileCacheStore.cs

```csharp
using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class CompileCacheStore
	{
		public static void Write(CompileCacheEntry entry)
		{
			string path = FilePath();
			string tempPath = path + ".tmp";
			File.WriteAllText(tempPath, JsonUtility.ToJson(entry));

			if (File.Exists(path))
			{
				File.Replace(tempPath, path, null);
			}
			else
			{
				File.Move(tempPath, path);
			}
		}

		public static bool TryRead(out CompileCacheEntry entry)
		{
			entry = null;
			string path = FilePath();
			if (!File.Exists(path))
			{
				return false;
			}

			try
			{
				entry = JsonUtility.FromJson<CompileCacheEntry>(File.ReadAllText(path));
			}
			catch
			{
				entry = null;
			}

			return entry != null;
		}

		private static string FilePath()
		{
			return Path.Combine(BridgePaths.WorkingRoot, "compile-cache.json");
		}
	}
}
```

### TaskRequestReader.cs

```csharp
using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class TaskRequestReader
	{
		public static bool TryRead(string taskFilePath, out TaskRequest request)
		{
			request = null;
			if (!File.Exists(taskFilePath))
			{
				return false;
			}

			try
			{
				request = JsonUtility.FromJson<TaskRequest>(File.ReadAllText(taskFilePath));
			}
			catch
			{
				request = null;
			}

			return request != null && !string.IsNullOrEmpty(request.Id);
		}
	}
}
```

### TaskFileHash.cs

Вынести из `TaskCoordinator` приватные `HashOf`, `ComputeHash` и словарь `_hashCache` без изменения логики:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge
{
	public static class TaskFileHash
	{
		private static readonly Dictionary<string, CachedHash> _hashCache = new Dictionary<string, CachedHash>();

		public static string HashOf(string taskFilePath, string payloadPath)
		{
			long taskFileLength = new FileInfo(taskFilePath).Length;
			long payloadLength = !string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath)
				? new FileInfo(payloadPath).Length
				: 0;
			string taskFileWriteUtc = File.GetLastWriteTimeUtc(taskFilePath).ToString("o");
			string payloadWriteUtc = !string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath)
				? File.GetLastWriteTimeUtc(payloadPath).ToString("o")
				: "";
			string cacheKey = taskFilePath + "|" + (payloadPath ?? "");

			CachedHash cached;
			if (_hashCache.TryGetValue(cacheKey, out cached)
				&& cached.TaskFileLength == taskFileLength
				&& cached.PayloadLength == payloadLength
				&& cached.TaskFileWriteUtc == taskFileWriteUtc
				&& cached.PayloadWriteUtc == payloadWriteUtc)
			{
				return cached.Hash;
			}

			string hash = ComputeHash(taskFilePath, payloadPath);
			_hashCache[cacheKey] = new CachedHash
			{
				TaskFileLength = taskFileLength,
				PayloadLength = payloadLength,
				TaskFileWriteUtc = taskFileWriteUtc,
				PayloadWriteUtc = payloadWriteUtc,
				Hash = hash
			};
			return hash;
		}

		private static string ComputeHash(string taskFilePath, string payloadPath)
		{
			using (SHA256 sha = SHA256.Create())
			{
				byte[] taskBytes = File.ReadAllBytes(taskFilePath);
				byte[] combined = taskBytes;

				if (!string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath))
				{
					byte[] payloadBytes = File.ReadAllBytes(payloadPath);
					combined = new byte[taskBytes.Length + payloadBytes.Length];
					Buffer.BlockCopy(taskBytes, 0, combined, 0, taskBytes.Length);
					Buffer.BlockCopy(payloadBytes, 0, combined, taskBytes.Length, payloadBytes.Length);
				}

				byte[] hashBytes = sha.ComputeHash(combined);
				var builder = new StringBuilder(hashBytes.Length * 2);
				foreach (byte b in hashBytes)
				{
					builder.Append(b.ToString("x2"));
				}

				return builder.ToString();
			}
		}
	}
}
```

### TestFilterCoverage.cs

```csharp
using System.Collections.Generic;

namespace AgentBridge
{
	public static class TestFilterCoverage
	{
		public static bool Covers(TestRunDump dump, TaskRequest request)
		{
			TestRunFilter filter = dump.Filter;

			if (IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames))
			{
				return true;
			}

			if (!IsEmpty(request.TestNames) && AllNamesPresent(dump.Entries, request.TestNames))
			{
				return true;
			}

			if (!IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames)
				&& !IsEmpty(request.AssemblyNames) && IsSubset(request.AssemblyNames, filter.AssemblyNames))
			{
				return true;
			}

			return SetsEqual(filter.AssemblyNames, request.AssemblyNames)
				&& SetsEqual(filter.TestNames, request.TestNames)
				&& SetsEqual(filter.CategoryNames, request.CategoryNames);
		}

		public static bool CoversFilterOnly(TestRunFilter filter, TaskRequest request)
		{
			if (IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames))
			{
				return true;
			}

			if (!IsEmpty(filter.AssemblyNames) && IsEmpty(filter.TestNames) && IsEmpty(filter.CategoryNames)
				&& !IsEmpty(request.AssemblyNames) && IsSubset(request.AssemblyNames, filter.AssemblyNames))
			{
				return true;
			}

			return SetsEqual(filter.AssemblyNames, request.AssemblyNames)
				&& SetsEqual(filter.TestNames, request.TestNames)
				&& SetsEqual(filter.CategoryNames, request.CategoryNames);
		}

		public static List<TestCaseResult> Select(List<TestCaseResult> entries, TaskRequest request)
		{
			var selected = new List<TestCaseResult>();
			var assemblies = ToSet(request.AssemblyNames);
			var names = ToSet(request.TestNames);
			var categories = ToSet(request.CategoryNames);

			foreach (TestCaseResult entry in entries)
			{
				if (assemblies != null && !assemblies.Contains(entry.Assembly))
				{
					continue;
				}

				if (names != null && !names.Contains(entry.FullName))
				{
					continue;
				}

				if (categories != null && !HasAnyCategory(entry, categories))
				{
					continue;
				}

				selected.Add(entry);
			}

			return selected;
		}

		private static bool HasAnyCategory(TestCaseResult entry, HashSet<string> categories)
		{
			foreach (string category in entry.Categories)
			{
				if (categories.Contains(category))
				{
					return true;
				}
			}

			return false;
		}

		private static bool AllNamesPresent(List<TestCaseResult> entries, string[] names)
		{
			var present = new HashSet<string>();
			foreach (TestCaseResult entry in entries)
			{
				present.Add(entry.FullName);
			}

			foreach (string name in names)
			{
				if (!present.Contains(name))
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsSubset(string[] inner, string[] outer)
		{
			var outerSet = new HashSet<string>(outer);
			foreach (string item in inner)
			{
				if (!outerSet.Contains(item))
				{
					return false;
				}
			}

			return true;
		}

		private static bool SetsEqual(string[] left, string[] right)
		{
			var leftSet = new HashSet<string>(left ?? new string[0]);
			var rightSet = new HashSet<string>(right ?? new string[0]);
			return leftSet.SetEquals(rightSet);
		}

		private static bool IsEmpty(string[] values)
		{
			return values == null || values.Length == 0;
		}

		private static HashSet<string> ToSet(string[] values)
		{
			if (values == null || values.Length == 0)
			{
				return null;
			}

			return new HashSet<string>(values);
		}
	}
}
```

### TestResultAggregator.cs

```csharp
using System.Collections.Generic;

namespace AgentBridge
{
	public static class TestResultAggregator
	{
		public static TestRunResult Aggregate(List<TestCaseResult> entries)
		{
			var run = new TestRunResult();

			foreach (TestCaseResult entry in entries)
			{
				run.duration += entry.DurationSeconds;

				switch (entry.Status)
				{
					case "Passed":
						run.passed++;
						break;
					case "Failed":
						run.failed++;
						AddFailure(run, entry);
						break;
					case "Skipped":
						run.skipped++;
						break;
					case "Inconclusive":
						run.inconclusive++;
						AddFailure(run, entry);
						break;
				}
			}

			run.total = run.passed + run.failed + run.skipped + run.inconclusive;
			return run;
		}

		public static string StatusOf(TestRunResult run)
		{
			return run.failed > 0 || run.inconclusive > 0 ? "test_failure" : "success";
		}

		private static void AddFailure(TestRunResult run, TestCaseResult entry)
		{
			run.failures.Add(new TestFailure
			{
				name = entry.FullName,
				message = entry.Message,
				stacktrace = entry.StackTrace
			});
		}
	}
}
```

### TestCacheQuery.cs

```csharp
namespace AgentBridge
{
	public static class TestCacheQuery
	{
		public static bool TryServe(TaskRequest request, out TestRunResult result, out string sourceTaskId, out string status)
		{
			result = null;
			sourceTaskId = null;
			status = null;

			string mode = request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
			TestRunDump dump;
			if (!TestRunDumpStore.TryRead(mode, out dump))
			{
				return false;
			}

			if (dump.Fingerprint != TestFingerprint.Current())
			{
				return false;
			}

			if (!TestFilterCoverage.Covers(dump, request))
			{
				return false;
			}

			result = TestResultAggregator.Aggregate(TestFilterCoverage.Select(dump.Entries, request));
			sourceTaskId = dump.SourceTaskId;
			status = TestResultAggregator.StatusOf(result);
			return true;
		}
	}
}
```

### CachedResultServer.cs

```csharp
using System;
using System.Collections.Generic;

namespace AgentBridge
{
	public static class CachedResultServer
	{
		public static void TryServePending(List<PendingTaskInfo> pending)
		{
			string compileFingerprint = null;

			for (int i = pending.Count - 1; i >= 0; i--)
			{
				PendingTaskInfo task = pending[i];
				if (task.Kind != "tests" && task.Kind != "compile")
				{
					continue;
				}

				TaskRecord existing;
				if (TaskJournal.TryRead(task.Id, out existing))
				{
					continue;
				}

				TaskRequest request;
				if (!TaskRequestReader.TryRead(task.TaskFilePath, out request) || request.Fresh)
				{
					continue;
				}

				if (task.Kind == "tests")
				{
					TestRunResult result;
					string sourceTaskId;
					string status;
					if (!TestCacheQuery.TryServe(request, out result, out sourceTaskId, out status))
					{
						continue;
					}

					TaskRecord record = BuildServedRecord(task, status, sourceTaskId);
					record.Tests = result;
					TaskJournal.Write(record);
				}
				else
				{
					CompileCacheEntry entry;
					if (!CompileCacheStore.TryRead(out entry))
					{
						continue;
					}

					if (compileFingerprint == null)
					{
						compileFingerprint = CompileFingerprint.Current();
					}

					if (entry.Fingerprint != compileFingerprint)
					{
						continue;
					}

					TaskRecord record = BuildServedRecord(task, entry.Status, entry.SourceTaskId);
					record.Diagnostics = entry.Diagnostics;
					record.ForeignErrors = entry.Diagnostics.Count > 0;
					TaskJournal.Write(record);
				}

				pending.RemoveAt(i);
			}
		}

		private static TaskRecord BuildServedRecord(PendingTaskInfo task, string status, string sourceTaskId)
		{
			string now = DateTime.UtcNow.ToString("o");
			var record = new TaskRecord
			{
				Id = task.Id,
				Kind = task.Kind,
				Status = status,
				Hash = TaskFileHash.HashOf(task.TaskFilePath, null),
				Cached = true,
				SourceTaskId = sourceTaskId,
				SessionId = BridgeStatusWriter.Current.SessionId,
				AgentSessionId = task.EffectiveSessionId,
				StartedAtUtc = now,
				FinishedAtUtc = now
			};

			record.Logs.Add("served from cache; source task " + sourceTaskId);
			return record;
		}
	}
}
```

### TestRunCoalescer.cs

```csharp
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class TestRunCoalescer
	{
		public static void TryAttachPending(List<PendingTaskInfo> pending)
		{
			string sourceId = SessionState.GetString(AgentTestRunner.CoordinatorTestTaskKey, "");
			if (string.IsNullOrEmpty(sourceId))
			{
				return;
			}

			string fingerprint = SessionState.GetString(AgentTestRunner.CoordinatorTestFingerprintKey, "");
			if (string.IsNullOrEmpty(fingerprint) || fingerprint != TestFingerprint.Current())
			{
				return;
			}

			string filterJson = SessionState.GetString(AgentTestRunner.CoordinatorTestFilterKey, "");
			if (string.IsNullOrEmpty(filterJson))
			{
				return;
			}

			TestRunFilter filter = JsonUtility.FromJson<TestRunFilter>(filterJson);
			if (filter == null)
			{
				return;
			}

			for (int i = pending.Count - 1; i >= 0; i--)
			{
				PendingTaskInfo task = pending[i];
				if (task.Kind != "tests" || task.Id == sourceId)
				{
					continue;
				}

				TaskRecord existing;
				if (TaskJournal.TryRead(task.Id, out existing))
				{
					continue;
				}

				TaskRequest request;
				if (!TaskRequestReader.TryRead(task.TaskFilePath, out request) || request.Fresh)
				{
					continue;
				}

				string mode = request.TestMode == "PlayMode" ? "PlayMode" : "EditMode";
				if (mode != filter.TestMode)
				{
					continue;
				}

				if (!TestFilterCoverage.CoversFilterOnly(filter, request))
				{
					continue;
				}

				var record = new TaskRecord
				{
					Id = task.Id,
					Kind = "tests",
					Status = "attached",
					AttachedToTaskId = sourceId,
					Hash = TaskFileHash.HashOf(task.TaskFilePath, null),
					SessionId = BridgeStatusWriter.Current.SessionId,
					AgentSessionId = task.EffectiveSessionId,
					StartedAtUtc = DateTime.UtcNow.ToString("o")
				};

				record.Logs.Add("attached to running test task " + sourceId);
				TaskJournal.Write(record);
				pending.RemoveAt(i);
			}
		}
	}
}
```

### TestRunAttachments.cs

```csharp
using System;
using System.IO;

namespace AgentBridge
{
	public static class TestRunAttachments
	{
		public static void Resolve(string sourceTaskId, TestRunDump dump)
		{
			foreach (TaskRecord record in Attached(sourceTaskId))
			{
				string taskFilePath = Path.Combine(BridgePaths.Inbox, record.Id + ".task.json");
				TaskRequest request;
				if (!TaskRequestReader.TryRead(taskFilePath, out request) || !TestFilterCoverage.Covers(dump, request))
				{
					TaskJournal.Delete(record.Id);
					continue;
				}

				TestRunResult result = TestResultAggregator.Aggregate(TestFilterCoverage.Select(dump.Entries, request));
				record.Tests = result;
				record.Status = TestResultAggregator.StatusOf(result);
				record.Cached = true;
				record.SourceTaskId = sourceTaskId;
				record.FinishedAtUtc = DateTime.UtcNow.ToString("o");
				record.Logs.Add("served from coalesced run " + sourceTaskId);
				TaskJournal.Write(record);
			}
		}

		public static void Requeue(string sourceTaskId)
		{
			foreach (TaskRecord record in Attached(sourceTaskId))
			{
				TaskJournal.Delete(record.Id);
			}
		}

		private static System.Collections.Generic.List<TaskRecord> Attached(string sourceTaskId)
		{
			var attached = new System.Collections.Generic.List<TaskRecord>();
			if (!Directory.Exists(BridgePaths.Journal))
			{
				return attached;
			}

			foreach (string file in Directory.GetFiles(BridgePaths.Journal, "*.json"))
			{
				TaskRecord record;
				if (!TaskJournal.TryRead(Path.GetFileNameWithoutExtension(file), out record))
				{
					continue;
				}

				if (record.Status == "attached" && record.AttachedToTaskId == sourceTaskId)
				{
					attached.Add(record);
				}
			}

			return attached;
		}
	}
}
```

## Editor/TaskRecord.cs

После поля `ForeignErrors` добавить:

```csharp
		public bool Cached;
		public string SourceTaskId;
		public string AttachedToTaskId;
```

## Editor/TaskRequest.cs

После поля `PlaySeconds` добавить:

```csharp
		public bool Fresh;
```

## Editor/TaskJournal.cs

- Добавить метод:

```csharp
		public static void Delete(string id)
		{
			string path = Path.Combine(BridgePaths.Journal, id + ".json");
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
```

- В `Trim`, в цикле сбора `records`, не добавлять записи со статусом `attached` (они живы и ждут результата):

```csharp
					if (record != null && record.Status != "attached")
					{
						records.Add(record);
					}
```

## Editor/AgentTestRunner.cs

- Добавить ключи рядом с существующими:

```csharp
		public const string CoordinatorTestFingerprintKey = "AgentBridge_CoordinatorTestFingerprint";
		public const string CoordinatorTestFilterKey = "AgentBridge_CoordinatorTestFilter";
```

- В `TryRequestRunForCoordinator`, сразу после существующих `SessionState.SetString(CoordinatorTestTaskKey, ...)` / `CoordinatorTestModeKey`, добавить:

```csharp
			SessionState.SetString(CoordinatorTestFingerprintKey, TestFingerprint.Current());
			SessionState.SetString(CoordinatorTestFilterKey, JsonUtility.ToJson(new TestRunFilter
			{
				TestMode = mode.ToString(),
				AssemblyNames = assemblyNames ?? new string[0],
				TestNames = testNames ?? new string[0],
				CategoryNames = categoryNames ?? new string[0]
			}));
```

- В обоих откатах внутри `TryRequestRunForCoordinator` (провал `TryVerifyClean` и блок `catch`) к существующим двум `EraseString` добавить:

```csharp
				SessionState.EraseString(CoordinatorTestFingerprintKey);
				SessionState.EraseString(CoordinatorTestFilterKey);
```

- В `TestCallbacks.RunFinished` убрать две строки `SessionState.EraseString(...)` (их забирает `FinalizeCoordinatorRun`) и перед веткой PlayMode-recovery записать провизорный дамп. Итоговое тело ветки:

```csharp
				string coordinatorTaskId = SessionState.GetString(CoordinatorTestTaskKey, "");
				if (!string.IsNullOrEmpty(coordinatorTaskId))
				{
					TestRunResult run = BuildResult(result);
					WritePendingDump(coordinatorTaskId, result);
					if (SessionState.GetString(CoordinatorTestModeKey, "") == TestMode.PlayMode.ToString()
						&& PlayModeSceneRecovery.IsPending)
					{
						PlayModeSceneRecovery.RecordResult(run);
						return;
					}

					FinalizeCoordinatorRun(coordinatorTaskId, run, null);
				}
```

- В `FinalizeRecoveredPlayModeRun` убрать две строки `SessionState.EraseString(...)` в начале.

- Добавить методы:

```csharp
		private static void WritePendingDump(string taskId, ITestResultAdaptor result)
		{
			string filterJson = SessionState.GetString(CoordinatorTestFilterKey, "");
			if (string.IsNullOrEmpty(filterJson))
			{
				return;
			}

			TestRunFilter filter = JsonUtility.FromJson<TestRunFilter>(filterJson);
			if (filter == null)
			{
				return;
			}

			var dump = new TestRunDump
			{
				Fingerprint = SessionState.GetString(CoordinatorTestFingerprintKey, ""),
				SourceTaskId = taskId,
				Filter = filter,
				FinishedAtUtc = System.DateTime.UtcNow.ToString("o")
			};

			CollectEntries(result, null, dump.Entries);
			TestRunDumpStore.WritePending(dump);
		}

		private static void CollectEntries(ITestResultAdaptor node, string assembly, List<TestCaseResult> entries)
		{
			if (node.Test != null && node.Test.FullName != null
				&& node.Test.FullName.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
			{
				assembly = System.IO.Path.GetFileNameWithoutExtension(node.Test.FullName);
			}

			if (node.HasChildren)
			{
				foreach (ITestResultAdaptor child in node.Children)
				{
					CollectEntries(child, assembly, entries);
				}

				return;
			}

			var entry = new TestCaseResult
			{
				FullName = node.FullName,
				Assembly = assembly ?? "",
				Status = node.TestStatus.ToString(),
				DurationSeconds = node.Duration,
				Message = node.Message,
				StackTrace = node.StackTrace
			};

			if (node.Test != null && node.Test.Categories != null)
			{
				entry.Categories.AddRange(node.Test.Categories);
			}

			entries.Add(entry);
		}
```

- Переписать `FinalizeCoordinatorRun`: он читает и гасит ключи SessionState, при недоступной записи чистит провизорный дамп и возвращает присоединённых в очередь, а после записи журнала либо промоутит дамп и раздаёт его присоединённым, либо возвращает их в очередь. Итоговый вид:

```csharp
		private static void FinalizeCoordinatorRun(string taskId, TestRunResult run, string recoveryError)
		{
			string testMode = SessionState.GetString(CoordinatorTestModeKey, "");
			SessionState.EraseString(CoordinatorTestTaskKey);
			SessionState.EraseString(CoordinatorTestModeKey);
			SessionState.EraseString(CoordinatorTestFingerprintKey);
			SessionState.EraseString(CoordinatorTestFilterKey);

			TaskRecord record;
			if (!TaskJournal.TryRead(taskId, out record))
			{
				TestRunDumpStore.DeletePending(testMode);
				TestRunAttachments.Requeue(taskId);
				return;
			}

			if (record.Logs == null)
			{
				record.Logs = new List<string>();
			}

			record.Logs.AddRange(SceneDirtyWatcher.DrainLogs());
			SceneDirtyWatcher.Disarm(taskId);

			record.Tests = run;
			if (run == null || run.aborted || !string.IsNullOrEmpty(recoveryError))
			{
				record.Status = "runtime_error";
				if (!string.IsNullOrEmpty(recoveryError))
				{
					record.Logs.Add(recoveryError);
				}
			}
			else
			{
				record.Status = run.failed > 0 || run.inconclusive > 0 ? "test_failure" : "success";
			}

			record.FinishedAtUtc = System.DateTime.UtcNow.ToString("o");
			TaskJournal.Write(record);

			TestRunDump dump;
			bool promoted = TestRunDumpStore.TryTakePending(testMode, out dump)
				&& dump.SourceTaskId == taskId
				&& run != null && !run.aborted && string.IsNullOrEmpty(recoveryError)
				&& dump.Fingerprint == TestFingerprint.Current();

			if (promoted)
			{
				TestRunDumpStore.Write(dump);
				TestRunAttachments.Resolve(taskId, dump);
			}
			else
			{
				TestRunAttachments.Requeue(taskId);
			}
		}
```

## Editor/CompileTaskExecutor.cs

- Добавить ключ:

```csharp
		public const string PendingCompileFingerprintKey = "AgentBridge_CompileFingerprint";
```

- В `Begin`, до `AssetDatabase.Refresh(...)`, добавить:

```csharp
			SessionState.SetString(PendingCompileFingerprintKey, CompileFingerprint.Current());
```

## Editor/TaskCoordinator.cs

- Добавить константу и поле:

```csharp
		private const float ServeIntervalSeconds = 1f;
		private static double _lastServeTime;
```

- Добавить метод:

```csharp
		private static void TryServeThrottled(double now)
		{
			if (now - _lastServeTime < ServeIntervalSeconds)
			{
				return;
			}

			_lastServeTime = now;
			List<PendingTaskInfo> pending = BuildPendingList(_activeTaskId);
			CachedResultServer.TryServePending(pending);
			TestRunCoalescer.TryAttachPending(pending);
		}
```

- В `OnUpdate`, в ветке `if (_activeTaskId != null)`, после `RefreshQueueStatus(now);` добавить `TryServeThrottled(now);`.
- В `OnUpdate`, ветку `if (PlayModeSceneRecovery.IsPending)` заменить на:

```csharp
			if (PlayModeSceneRecovery.IsPending)
			{
				TryServeThrottled(now);
				return;
			}
```

- В `OnUpdate`, в ветке `if (EditorApplication.isPlayingOrWillChangePlaymode)`, первой строкой добавить `TryServeThrottled(now);`.
- В `TryStartNextTask`, сразу после `List<PendingTaskInfo> pending = BuildPendingList(null);`, добавить:

```csharp
			CachedResultServer.TryServePending(pending);
```

- В `BuildPendingList` переписать блок проверки существующей записи (учёт `attached`):

```csharp
				TaskRecord existing;
				if (TaskJournal.TryRead(id, out existing))
				{
					if (existing.Status == "attached")
					{
						continue;
					}

					if (IsTerminal(existing.Status))
					{
						string payloadPath = PayloadPathOf(file);
						if (existing.Hash == TaskFileHash.HashOf(file, payloadPath))
						{
							continue;
						}
					}
				}
```

- В `FinalizeOrphanRecords`, после `if (IsTerminal(record.Status)) { continue; }`, добавить:

```csharp
				if (record.Status == "attached")
				{
					if (!string.IsNullOrEmpty(testTaskId) && record.AttachedToTaskId == testTaskId)
					{
						continue;
					}

					TaskJournal.Delete(record.Id);
					continue;
				}
```

- В `TryFinalizePendingCompileTask`, после `AgentSessionScheduler.OnTaskFinished(...)`, добавить:

```csharp
			string startFingerprint = SessionState.GetString(CompileTaskExecutor.PendingCompileFingerprintKey, "");
			SessionState.EraseString(CompileTaskExecutor.PendingCompileFingerprintKey);
			if ((record.Status == "success" || record.Status == "compiler_error")
				&& !string.IsNullOrEmpty(startFingerprint)
				&& startFingerprint == CompileFingerprint.Current())
			{
				CompileCacheStore.Write(new CompileCacheEntry
				{
					Fingerprint = startFingerprint,
					SourceTaskId = record.Id,
					Status = record.Status,
					Diagnostics = record.Diagnostics,
					FinishedAtUtc = record.FinishedAtUtc
				});
			}
```

- В `PollCompileTask` (таймаут), перед `FinishTask(...)`, добавить:

```csharp
			SessionState.EraseString(CompileTaskExecutor.PendingCompileFingerprintKey);
```

- Удалить из `TaskCoordinator` приватные `HashOf`, `ComputeHash` и поле `_hashCache`; все вызовы `HashOf(` заменить на `TaskFileHash.HashOf(` (места: `BuildPendingList`, `StartTask`, `RejectTaskFile`, ветка `release` в `TryStartPlaySessionTask`).

## AgentBridgeCli/TaskRequest.cs

Добавить свойство:

```csharp
	public bool Fresh { get; set; }
```

## AgentBridgeCli/CliOptions.cs

- Добавить свойство `public bool Fresh { get; private set; }`.
- В цикл `Parse` добавить ветку:

```csharp
			if (argument == "--fresh")
			{
				options.Fresh = true;
				continue;
			}
```

## AgentBridgeCli/BridgeClient.cs

- `SubmitCompileAsync(int waitSeconds)` заменить на `SubmitCompileAsync(int waitSeconds, bool fresh)`; в конструирование запроса добавить `Fresh = fresh`.
- `SubmitTestsAsync(...)` — добавить последний параметр `bool fresh`; в запрос добавить `Fresh = fresh`.

## AgentBridgeCli/AgentBridgeApplication.cs

- В `case "compile":` вызов заменить на `client.SubmitCompileAsync(options.WaitSeconds, options.Fresh)`, usage-строку — на `usage: agentbridge compile [--fresh] [--project <path>] [--wait <seconds>] [--format json|human]`.
- В `case "tests":` вызов заменить на `client.SubmitTestsAsync(mode, assemblies, tests, categories, options.WaitSeconds, options.Fresh)`.
- В `TryParseTests` usage-строку заменить на `usage: agentbridge tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C] [--fresh]`.
- В `WriteHelp()` заменить строки команд:

```
  compile [--fresh]
  tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C] [--fresh]
```

и в блок global options добавить:

```
  --fresh            force a real run for tests/compile, ignore cached results
```

## AgentBridgeCli/TaskResultFormatter.cs

В `FormatHuman(JsonElement root)`, сразу после блока, добавляющего `id` в `details`, добавить:

```csharp
		if (root.TryGetProperty("Cached", out var cachedElement) && cachedElement.ValueKind == JsonValueKind.True)
		{
			var sourceTaskId = GetString(root, "SourceTaskId");
			details.Add(string.IsNullOrWhiteSpace(sourceTaskId) ? "cached" : "cached from " + sourceTaskId);
		}
```

## unity-bridge-plugin/skills/unity-bridge/SKILL.md

- В разделе `### compile` заменить строку с bash-примером и добавить абзац после существующего текста раздела:

```bash
agentbridge compile --format human
agentbridge compile --fresh --format human
```

```markdown
Повторный `compile` при неизменных исходниках отвечается из кэша мгновенно: в результате будет `Cached: true` и `SourceTaskId` исходного рана. Кэш инвалидируется любым изменением `*.cs`, `*.asmdef`, `*.asmref`, `*.rsp`, манифеста пакетов или ProjectSettings. `--fresh` принудительно запускает настоящую компиляцию.
```

- Заголовок раздела tests заменить на `### tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C] [--fresh]` и дополнить раздел абзацами:

```markdown
**Контракт: тесты обязаны быть контекстно-независимыми.** Не полагайся в тестах на открытые сцены, selection или другое состояние редактора: мост переключает сценовые контексты между сессиями и раздаёт результаты тестов между агентами. Тест, зависящий от контекста, будет давать разные результаты разным сессиям. Нужна сцена — открывай её из самого теста.

Мост кэширует результаты: если с последнего рана ничего не изменилось (ни кода, ни ассетов), повторный `tests` отвечается из кэша мгновенно, без очереди — в результате `Cached: true` и `SourceTaskId`. Запрос-подмножество (например, одна сборка после полного рана) тоже обслуживается из кэша. Если совместимый ран прямо сейчас идёт у другого агента, твой таск присоединяется к нему и получает его результат. Любое изменение проекта инвалидирует кэш автоматически — устаревший результат получить нельзя. Перезапустить флаки-тест без изменения кода: `--fresh`.
```

- В раздел `## Работа рядом с другими агентами` добавить пункт списка:

```markdown
- `tests` и `compile` без изменений в проекте не занимают редактор: они отвечаются из кэша или присоединяются к идущему рану. Не бойся спрашивать повторно — дорогим является только ран после реальных изменений.
```

## Отклонения от плана (по факту реализации)

Схема отпечатка тестов из плана не заработала, и её пришлось переделать. Замеры в живом редакторе:

- `AssetDatabase.GlobalArtifactDependencyVersion` двигается **самим тестовым прогоном**: тесты этого проекта создают, сохраняют и удаляют сцены и префабы, и счётчик за один ран EditMode прошёл 167 → 202. Отпечаток, снятый на старте, не совпадал на финализации **никогда**, поэтому в кэш не попадал ни один прогон.
- Счётчик отражает только то, что Unity **уже импортировала**. Файл, отредактированный на диске и ещё не импортированный, его не двигает — а это и есть основной сценарий (агент правит код и тут же просит тесты). Проверено: после дописывания строки в тестовый `.cs` кэш отдавал устаревший результат.

Итоговая схема — два отпечатка вместо одного:

- **Ключ кэша** = `<pid>-<processStartTicks>-<GlobalArtifactDependencyVersion>`, но снимается **на промоушене** (после рана и после PlayMode scene recovery), а не на старте. Ловит изменения ассетов.
- **Отпечаток исходников** = хэш `CompileFingerprint` (тот же, что у `compile`). Ловит правки кода на диске до импорта и не двигается от того, что тесты трогают ассеты. Он же служит гардом «исходники не менялись за время рана» (сверка старт/финализация) и условием присоединения в `TestRunCoalescer`.

Кэш-хит требует совпадения **обоих**. `TestRunDump` получил поле `SourceFingerprint`; `AgentTestRunner.CoordinatorTestFingerprintKey` заменён на `CoordinatorTestSourceKey`.

Прочее:

- `CompileFingerprint` намеренно **не мемоизируется**. Промежуточная версия с memo на 1 секунду отдавала закэшированный `compile` после удаления файла — окно memo ровно совпадает с паузой «правка → проверка». Взамен обход дерева сведён к одному проходу с фильтром по расширению, и хэш считается только когда в очереди реально висит `tests`/`compile`-таск.
- Известное ограничение: в проекте, чьи тесты меняют ассеты, чередование EditMode и PlayMode сбрасывает кэш соседнего режима (общий счётчик двигает чужой прогон). Повторы в одном режиме кэшируются штатно. Отказ консервативный — лишний ран, но никогда не неверный результат.

Проверено вживую: кэш и инвалидация для `compile`, EditMode и PlayMode; подмножества по `--assembly` и `--test`; `--fresh`; кэширование `test_failure` (с failures и кодом выхода 1); коалесценция двух сессий (`attached` → `served from coalesced run`).

## После выполнения

- Поменяй статус в начале этого документа на `Выполнено`.
- Уточни у заказчика, нужно ли обновить документацию проекта под эти изменения.

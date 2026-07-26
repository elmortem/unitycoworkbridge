using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace AgentBridge
{
	public class CSharpTaskExecutor
	{
		public string StatusHint { get; private set; }

		private readonly Task<CSharpTaskOutcome> _task;

		private CSharpTaskExecutor(string source, string sourcePath, string taskId, CancellationToken cancellationToken)
		{
			StatusHint = "compiling";
			_task = RunAsync(source, sourcePath, taskId, cancellationToken);
		}

		public static CSharpTaskExecutor Begin(string source, string sourcePath, string taskId, CancellationToken cancellationToken)
		{
			return new CSharpTaskExecutor(source, sourcePath, taskId, cancellationToken);
		}

		public bool IsCompleted
		{
			get { return _task.IsCompleted; }
		}

		public CSharpTaskOutcome GetResult()
		{
			if (_task.IsFaulted)
			{
				Exception inner = _task.Exception != null ? _task.Exception.GetBaseException() : null;
				return new CSharpTaskOutcome
				{
					Status = "runtime_error",
					ErrorMessage = inner != null ? inner.Message : "unknown error"
				};
			}

			if (_task.IsCanceled)
			{
				return new CSharpTaskOutcome { Status = "canceled" };
			}

			return _task.Result;
		}

		private async Task<CSharpTaskOutcome> RunAsync(string source, string sourcePath, string taskId, CancellationToken cancellationToken)
		{
			CompileResult compileResult = await Task.Run(() => RoslynCompiler.Compile(source, sourcePath, taskId, cancellationToken), cancellationToken);

			if (!compileResult.Success)
			{
				return new CSharpTaskOutcome
				{
					Status = compileResult.GuardrailRejected ? "rejected" : "compiler_error",
					CompileResult = compileResult
				};
			}

			MethodInfo method;
			bool needsToken;
			string error;
			if (!TaskMethodResolver.TryResolve(compileResult.Assembly, taskId, out method, out needsToken, out error))
			{
				return new CSharpTaskOutcome
				{
					Status = "rejected",
					ErrorMessage = error,
					CompileResult = compileResult
				};
			}

			StatusHint = "running";

			Task<string> invokeTask = await MainThreadDispatcher.Enqueue(() =>
			{
				object[] args = needsToken ? new object[] { cancellationToken } : null;
				return (Task<string>)method.Invoke(null, args);
			});

			string returnValue = await invokeTask;

			return new CSharpTaskOutcome
			{
				Status = "success",
				ReturnValue = returnValue,
				CompileResult = compileResult
			};
		}
	}
}

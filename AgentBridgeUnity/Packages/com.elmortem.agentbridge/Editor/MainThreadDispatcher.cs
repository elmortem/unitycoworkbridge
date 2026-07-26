using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class MainThreadDispatcher
	{
		private class Item
		{
			public Action Execute;
			public Action Cancel;
		}

		private const int MaxItemsPerTick = 16;

		private static readonly ConcurrentQueue<Item> _queue = new ConcurrentQueue<Item>();

		static MainThreadDispatcher()
		{
			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;

			AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
			AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
		}

		public static void Enqueue(Action action)
		{
			_queue.Enqueue(new Item
			{
				Execute = action,
				Cancel = null
			});
		}

		public static Task<T> Enqueue<T>(Func<T> func)
		{
			var tcs = new TaskCompletionSource<T>();

			_queue.Enqueue(new Item
			{
				Execute = () =>
				{
					try
					{
						T result = func();
						tcs.TrySetResult(result);
					}
					catch (Exception ex)
					{
						tcs.TrySetException(ex);
					}
				},
				Cancel = () => tcs.TrySetCanceled()
			});

			return tcs.Task;
		}

		private static void OnUpdate()
		{
			int processed = 0;
			Item item;
			while (processed < MaxItemsPerTick && _queue.TryDequeue(out item))
			{
				try
				{
					item.Execute();
				}
				catch (Exception ex)
				{
					Debug.LogException(ex);
				}

				processed++;
			}
		}

		private static void OnBeforeAssemblyReload()
		{
			Item item;
			while (_queue.TryDequeue(out item))
			{
				if (item.Cancel != null)
				{
					item.Cancel();
				}
			}
		}
	}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace AgentBridge
{
	public static class RoslynCompiler
	{
		private static int _counter;

		public static CompileResult Compile(string source, string sourcePath, string taskId, CancellationToken cancellationToken)
		{
			var result = new CompileResult();

			if (!RoslynResolver.IsReady)
			{
				RoslynResolver.ResolveConfigured();
			}

			if (!RoslynResolver.IsReady)
			{
				result.Success = false;
				result.Diagnostics.Add(new TaskDiagnostic
				{
					Code = "roslyn_unavailable",
					Severity = "Error",
					Message = "No Roslyn source is available",
					File = sourcePath
				});
				return result;
			}

			object tree = ParseText(source, sourcePath, cancellationToken);

			List<GuardrailViolation> violations;
			if (!SourceGuardrail.TryValidate(tree, out violations))
			{
				result.Success = false;
				result.GuardrailRejected = true;

				foreach (GuardrailViolation violation in violations)
				{
					result.Diagnostics.Add(new TaskDiagnostic
					{
						Code = "guardrail",
						Severity = "Error",
						Message = violation.Reason,
						File = sourcePath,
						Line = violation.Line,
						Column = violation.Column
					});
				}

				return result;
			}

			int counter = Interlocked.Increment(ref _counter);
			string assemblyName = "AgentTask_" + taskId + "_" + counter;

			dynamic compilation = CreateCompilation(assemblyName, tree);
			bool emitPdb = AgentBridgeSettingsStore.GetEmitPdb();
			dynamic emitOptions = CreatePortableEmitOptions();

			using (var peStream = new MemoryStream())
			{
				dynamic emitResult;

				if (emitPdb)
				{
					using (var pdbStream = new MemoryStream())
					{
						emitResult = compilation.Emit(peStream, pdbStream, options: emitOptions, cancellationToken: cancellationToken);
						CollectDiagnostics(emitResult, sourcePath, result.Diagnostics);

						if (!(bool)emitResult.Success)
						{
							result.Success = false;
							return result;
						}

						result.Assembly = Assembly.Load(peStream.ToArray(), pdbStream.ToArray());
					}
				}
				else
				{
					emitResult = compilation.Emit(peStream, options: emitOptions, cancellationToken: cancellationToken);
					CollectDiagnostics(emitResult, sourcePath, result.Diagnostics);

					if (!(bool)emitResult.Success)
					{
						result.Success = false;
						return result;
					}

					result.Assembly = Assembly.Load(peStream.ToArray());
				}
			}

			result.Success = true;
			return result;
		}

		private static void CollectDiagnostics(dynamic emitResult, string sourcePath, List<TaskDiagnostic> diagnostics)
		{
			foreach (object diagnosticObj in (IEnumerable)emitResult.Diagnostics)
			{
				dynamic diagnostic = diagnosticObj;
				string severity = diagnostic.Severity.ToString();
				if (severity != "Error" && severity != "Warning")
				{
					continue;
				}

				int line = 0;
				int column = 0;

				try
				{
					dynamic lineSpan = diagnostic.Location.GetLineSpan();
					line = (int)lineSpan.StartLinePosition.Line + 1;
					column = (int)lineSpan.StartLinePosition.Character + 1;
				}
				catch
				{
				}

				diagnostics.Add(new TaskDiagnostic
				{
					Code = diagnostic.Id.ToString(),
					Severity = severity,
					Message = diagnostic.GetMessage().ToString(),
					File = sourcePath,
					Line = line,
					Column = column
				});
			}
		}

		private static object CreatePortableEmitOptions()
		{
			Type emitOptionsType = RoslynResolver.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.Emit.EmitOptions");
			Type debugFormatType = RoslynResolver.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.Emit.DebugInformationFormat");

			object portable = Enum.Parse(debugFormatType, "PortablePdb");

			ConstructorInfo ctor = RoslynReflectionHelper.FindConstructorWithParameterName(emitOptionsType, "debugInformationFormat");
			var overrides = new Dictionary<string, object> { { "debugInformationFormat", portable } };
			object[] args = RoslynReflectionHelper.BuildArgsAllNamed(ctor, overrides);
			return ctor.Invoke(args);
		}

		private static object ParseText(string source, string path, CancellationToken cancellationToken)
		{
			Type syntaxTreeType = RoslynResolver.CodeAnalysisCSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
			Type parseOptionsType = RoslynResolver.CodeAnalysisCSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");
			Type languageVersionType = RoslynResolver.CodeAnalysisCSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion");

			dynamic latest = Enum.Parse(languageVersionType, "Latest");
			object defaultOptions = RoslynReflectionHelper.GetStaticMember(parseOptionsType, "Default");
			dynamic defaultOptionsDyn = defaultOptions;
			dynamic parseOptions = defaultOptionsDyn.WithLanguageVersion(latest);

			MethodInfo parseTextMethod = RoslynReflectionHelper.FindBestOverload(syntaxTreeType, "ParseText", BindingFlags.Public | BindingFlags.Static, typeof(string));

			var overrides = new Dictionary<string, object>
			{
				{ "options", (object)parseOptions },
				{ "path", path ?? "" },
				{ "encoding", System.Text.Encoding.UTF8 },
				{ "cancellationToken", cancellationToken }
			};

			object[] args = RoslynReflectionHelper.BuildArgs(parseTextMethod, source, overrides);
			return parseTextMethod.Invoke(null, args);
		}

		private static object CreateCompilation(string assemblyName, object tree)
		{
			Type compilationType = RoslynResolver.CodeAnalysisCSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
			Type optionsType = RoslynResolver.CodeAnalysisCSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
			Type outputKindType = RoslynResolver.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.OutputKind");
			Type optimizationLevelType = RoslynResolver.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.OptimizationLevel");

			object dllOutputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
			object debugLevel = Enum.Parse(optimizationLevelType, "Debug");

			ConstructorInfo optionsCtor = RoslynReflectionHelper.FindBestConstructor(optionsType, outputKindType);
			var optionsOverrides = new Dictionary<string, object> { { "optimizationLevel", debugLevel } };
			object[] optionsArgs = RoslynReflectionHelper.BuildArgs(optionsCtor, dllOutputKind, optionsOverrides);
			object compilationOptions = optionsCtor.Invoke(optionsArgs);

			object syntaxTreeList = CreateSyntaxTreeList(tree);
			object referenceList = CreateReferenceList();

			MethodInfo createMethod = RoslynReflectionHelper.FindBestOverload(compilationType, "Create", BindingFlags.Public | BindingFlags.Static, typeof(string));
			var createOverrides = new Dictionary<string, object>
			{
				{ "syntaxTrees", syntaxTreeList },
				{ "references", referenceList },
				{ "options", compilationOptions }
			};

			object[] createArgs = RoslynReflectionHelper.BuildArgs(createMethod, assemblyName, createOverrides);
			return createMethod.Invoke(null, createArgs);
		}

		private static object CreateSyntaxTreeList(object tree)
		{
			Type syntaxTreeType = RoslynResolver.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.SyntaxTree");
			object list = RoslynReflectionHelper.CreateGenericList(syntaxTreeType);
			((IList)list).Add(tree);
			return list;
		}

		private static object CreateReferenceList()
		{
			Type metadataReferenceType = RoslynResolver.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataReference");
			object list = RoslynReflectionHelper.CreateGenericList(metadataReferenceType);
			IList ilist = (IList)list;

			foreach (object reference in ReferenceCatalog.GetReferences())
			{
				ilist.Add(reference);
			}

			return list;
		}
	}
}

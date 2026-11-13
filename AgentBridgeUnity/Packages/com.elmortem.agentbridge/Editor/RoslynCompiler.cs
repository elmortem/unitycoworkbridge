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

			// One pair for the whole compile, even if the source is switched meanwhile.
			RoslynLocation roslyn = RoslynResolver.Active;
			if (roslyn == null)
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

			object tree = ParseText(roslyn, source, sourcePath, cancellationToken);

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

			dynamic compilation = CreateCompilation(roslyn, assemblyName, tree);
			bool emitPdb = AgentBridgeSettingsStore.GetEmitPdb();
			dynamic emitOptions = CreatePortableEmitOptions(roslyn);

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

						result.Assembly = AssemblyCompat.LoadFromBytes(peStream.ToArray(), pdbStream.ToArray());
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

					result.Assembly = AssemblyCompat.LoadFromBytes(peStream.ToArray(), null);
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

		private static object CreatePortableEmitOptions(RoslynLocation roslyn)
		{
			Type emitOptionsType = RequireType(roslyn, roslyn.CodeAnalysisAssembly, "Microsoft.CodeAnalysis.Emit.EmitOptions");
			Type debugFormatType = RequireType(roslyn, roslyn.CodeAnalysisAssembly, "Microsoft.CodeAnalysis.Emit.DebugInformationFormat");

			object portable = Enum.Parse(debugFormatType, "PortablePdb");

			ConstructorInfo ctor = Require(roslyn, RoslynReflectionHelper.FindConstructorWithParameterName(emitOptionsType, "debugInformationFormat"),
				"EmitOptions(debugInformationFormat)");
			var overrides = new Dictionary<string, object> { { "debugInformationFormat", portable } };
			object[] args = RoslynReflectionHelper.BuildArgsAllNamed(ctor, overrides);
			return ctor.Invoke(args);
		}

		private static object ParseText(RoslynLocation roslyn, string source, string path, CancellationToken cancellationToken)
		{
			Type syntaxTreeType = RequireType(roslyn, roslyn.CSharpAssembly, "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
			Type parseOptionsType = RequireType(roslyn, roslyn.CSharpAssembly, "Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");
			Type languageVersionType = RequireType(roslyn, roslyn.CSharpAssembly, "Microsoft.CodeAnalysis.CSharp.LanguageVersion");

			dynamic latest = Enum.Parse(languageVersionType, "Latest");
			object defaultOptions = Require(roslyn, RoslynReflectionHelper.GetStaticMember(parseOptionsType, "Default"), "CSharpParseOptions.Default");
			dynamic defaultOptionsDyn = defaultOptions;
			dynamic parseOptions = defaultOptionsDyn.WithLanguageVersion(latest);

			MethodInfo parseTextMethod = Require(roslyn,
				RoslynReflectionHelper.FindBestOverload(syntaxTreeType, "ParseText", BindingFlags.Public | BindingFlags.Static, typeof(string)),
				"CSharpSyntaxTree.ParseText(string)");

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

		private static object CreateCompilation(RoslynLocation roslyn, string assemblyName, object tree)
		{
			Type compilationType = RequireType(roslyn, roslyn.CSharpAssembly, "Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
			Type optionsType = RequireType(roslyn, roslyn.CSharpAssembly, "Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
			Type outputKindType = RequireType(roslyn, roslyn.CodeAnalysisAssembly, "Microsoft.CodeAnalysis.OutputKind");
			Type optimizationLevelType = RequireType(roslyn, roslyn.CodeAnalysisAssembly, "Microsoft.CodeAnalysis.OptimizationLevel");

			object dllOutputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
			object debugLevel = Enum.Parse(optimizationLevelType, "Debug");

			// The first place where the two assemblies meet: a miss means they come from different copies of Roslyn.
			ConstructorInfo optionsCtor = Require(roslyn, RoslynReflectionHelper.FindBestConstructor(optionsType, outputKindType),
				"CSharpCompilationOptions(OutputKind)");
			var optionsOverrides = new Dictionary<string, object> { { "optimizationLevel", debugLevel } };
			object[] optionsArgs = RoslynReflectionHelper.BuildArgs(optionsCtor, dllOutputKind, optionsOverrides);
			object compilationOptions = optionsCtor.Invoke(optionsArgs);

			object syntaxTreeList = CreateSyntaxTreeList(roslyn, tree);
			object referenceList = CreateReferenceList(roslyn);

			MethodInfo createMethod = Require(roslyn,
				RoslynReflectionHelper.FindBestOverload(compilationType, "Create", BindingFlags.Public | BindingFlags.Static, typeof(string)),
				"CSharpCompilation.Create(string)");
			var createOverrides = new Dictionary<string, object>
			{
				{ "syntaxTrees", syntaxTreeList },
				{ "references", referenceList },
				{ "options", compilationOptions }
			};

			object[] createArgs = RoslynReflectionHelper.BuildArgs(createMethod, assemblyName, createOverrides);
			return createMethod.Invoke(null, createArgs);
		}

		private static object CreateSyntaxTreeList(RoslynLocation roslyn, object tree)
		{
			Type syntaxTreeType = RequireType(roslyn, roslyn.CodeAnalysisAssembly, "Microsoft.CodeAnalysis.SyntaxTree");
			object list = RoslynReflectionHelper.CreateGenericList(syntaxTreeType);
			((IList)list).Add(tree);
			return list;
		}

		private static object CreateReferenceList(RoslynLocation roslyn)
		{
			Type metadataReferenceType = RequireType(roslyn, roslyn.CodeAnalysisAssembly, "Microsoft.CodeAnalysis.MetadataReference");
			object list = RoslynReflectionHelper.CreateGenericList(metadataReferenceType);
			IList ilist = (IList)list;

			foreach (object reference in ReferenceCatalog.GetReferences(roslyn))
			{
				ilist.Add(reference);
			}

			return list;
		}

		private static Type RequireType(RoslynLocation roslyn, Assembly assembly, string name)
		{
			return Require(roslyn, assembly.GetType(name), name);
		}

		// Roslyn is reached by reflection, so a missing member would otherwise surface as a bare
		// NullReferenceException with no hint which copy of Roslyn is at fault.
		private static T Require<T>(RoslynLocation roslyn, T value, string member) where T : class
		{
			if (value != null)
			{
				return value;
			}

			throw new InvalidOperationException("Roslyn API not found: " + member + ". Source " + roslyn.Kind
				+ ", C# compiler " + Describe(roslyn.CSharpAssembly) + ", Microsoft.CodeAnalysis " + Describe(roslyn.CodeAnalysisAssembly));
		}

		private static string Describe(Assembly assembly)
		{
			if (assembly == null)
			{
				return "none";
			}

			string path = AssemblyCompat.GetAssemblyPath(assembly);
			return assembly.GetName().Version + (string.IsNullOrEmpty(path) ? "" : " (" + path + ")");
		}
	}
}

using System;
using System.Collections.Generic;

namespace AgentBridge
{
	public static class SourceGuardrail
	{
		private static Type _syntaxKindType;
		private static readonly Dictionary<string, int> _kindCache = new Dictionary<string, int>();

		public static bool TryValidate(object syntaxTree, out List<GuardrailViolation> violations)
		{
			violations = new List<GuardrailViolation>();

			if (syntaxTree == null || !RoslynResolver.IsReady)
			{
				return true;
			}

			dynamic tree = syntaxTree;
			dynamic root = tree.GetRoot();

			var allNodes = new List<object>();
			allNodes.Add((object)root);
			CollectDescendants(root, allNodes);

			HashSet<string> testRunnerApiVariables = CollectTestRunnerApiVariables(allNodes);

			foreach (object nodeObj in allNodes)
			{
				dynamic node = nodeObj;

				if (IsKind(node, "InvocationExpression"))
				{
					CheckWaitCall(node, violations);
					CheckGetAwaiterGetResult(node, violations);
					CheckThreadSleep(node, violations);
					CheckForbiddenCall(node, testRunnerApiVariables, violations);
				}
				else if (IsKind(node, "SimpleMemberAccessExpression"))
				{
					CheckResultAccess(node, violations);
				}
				else if (IsKind(node, "SimpleAssignmentExpression"))
				{
					CheckPlayModeAssignment(node, violations);
				}
				else if (IsKind(node, "WhileStatement"))
				{
					CheckWhileTrue(node, violations);
				}
				else if (IsKind(node, "ForStatement"))
				{
					CheckForEver(node, violations);
				}
				else if (IsKind(node, "StringLiteralExpression"))
				{
					CheckPlayModeLiteral(node, violations);
				}
			}

			return violations.Count == 0;
		}

		private const string ModalApiReason = "modal or interactive editor API is not allowed in agent tasks";

		private const string PlayModeReason =
			"play mode control is not allowed in agent tasks; use agentbridge play/stopplay";

		// Reflection and the menu are the two cheap ways around the direct EnterPlaymode call,
		// and both need one of these names spelled out as a string.
		private static readonly HashSet<string> PlayModeLiterals = new HashSet<string>(StringComparer.Ordinal)
		{
			"EnterPlaymode",
			"ExitPlaymode",
			"EnterPlayMode",
			"ExitPlayMode",
			"isPlaying",
			"Edit/Play"
		};

		private static void CheckForbiddenCall(dynamic invocation, HashSet<string> testRunnerApiVariables,
			List<GuardrailViolation> violations)
		{
			dynamic expr = invocation.Expression;
			if (!IsKind(expr, "SimpleMemberAccessExpression"))
			{
				return;
			}

			string target = expr.Expression.ToString();
			string typeName = LastSegment(target);
			string methodName = (string)expr.Name.Identifier.Text;

			bool editorTransition = typeName == "EditorSceneManager"
				&& (methodName == "OpenScene"
					|| methodName == "NewScene"
					|| methodName == "CloseScene"
					|| methodName == "RestoreSceneManagerSetup");
			bool runtimeTransition = typeName == "SceneManager"
				&& (methodName == "LoadScene"
					|| methodName == "LoadSceneAsync"
					|| methodName == "UnloadSceneAsync");

			if (editorTransition || runtimeTransition)
			{
				AddViolation(violations, invocation, "direct scene transition is not allowed; use AgentBridge.AgentSceneManager");
				return;
			}

			if (typeName == "EditorApplication" && methodName == "ExecuteMenuItem")
			{
				AddViolation(violations, invocation, "ExecuteMenuItem is not allowed in agent tasks");
				return;
			}

			if (IsModalCall(typeName, methodName) || IsTestRunnerExecute(target, methodName, testRunnerApiVariables))
			{
				AddViolation(violations, invocation, ModalApiReason);
			}
		}

		private static void CheckPlayModeLiteral(dynamic literal, List<GuardrailViolation> violations)
		{
			var value = (string)literal.Token.ValueText;
			if (value == null || !PlayModeLiterals.Contains(value))
			{
				return;
			}

			AddViolation(violations, literal, PlayModeReason);
		}

		private static bool IsModalCall(string typeName, string methodName)
		{
			switch (typeName)
			{
				case "EditorSceneManager":
					return methodName == "SaveCurrentModifiedScenesIfUserWantsTo"
						|| methodName == "SaveModifiedScenesIfUserWantsTo";
				case "EditorApplication":
					return methodName == "EnterPlaymode"
						|| methodName == "ExitPlaymode"
						|| methodName == "Exit";
				case "EditorUtility":
					return methodName == "DisplayDialog"
						|| methodName == "DisplayDialogComplex"
						|| methodName == "OpenFilePanel"
						|| methodName == "OpenFolderPanel"
						|| methodName == "SaveFilePanel"
						|| methodName == "SaveFilePanelInProject";
				case "PrefabStageUtility":
					return methodName == "OpenPrefab";
				case "AssetDatabase":
					return methodName == "OpenAsset";
				default:
					return false;
			}
		}

		private static bool IsTestRunnerExecute(string target, string methodName, HashSet<string> testRunnerApiVariables)
		{
			if (methodName != "Execute")
			{
				return false;
			}

			// TestRunnerApi.Execute is an instance method, so the receiver is usually a local.
			// Without a semantic model the declaration text is what ties the local to the type.
			return target.IndexOf("TestRunnerApi", StringComparison.Ordinal) >= 0
				|| testRunnerApiVariables.Contains(target);
		}

		private static HashSet<string> CollectTestRunnerApiVariables(List<object> allNodes)
		{
			var names = new HashSet<string>(StringComparer.Ordinal);

			foreach (object nodeObj in allNodes)
			{
				dynamic node = nodeObj;
				if (!IsKind(node, "VariableDeclaration"))
				{
					continue;
				}

				string declaration = node.ToString();
				if (declaration.IndexOf("TestRunnerApi", StringComparison.Ordinal) < 0)
				{
					continue;
				}

				foreach (dynamic variable in node.Variables)
				{
					names.Add((string)variable.Identifier.Text);
				}
			}

			return names;
		}

		private static void CheckPlayModeAssignment(dynamic assignment, List<GuardrailViolation> violations)
		{
			dynamic left = assignment.Left;
			if (!IsKind(left, "SimpleMemberAccessExpression"))
			{
				return;
			}

			string typeName = LastSegment(left.Expression.ToString());
			string memberName = (string)left.Name.Identifier.Text;

			if (typeName == "EditorApplication" && (memberName == "isPlaying" || memberName == "isPaused"))
			{
				AddViolation(violations, assignment, ModalApiReason);
			}
		}

		private static string LastSegment(string expression)
		{
			int lastDot = expression.LastIndexOf('.');
			return lastDot >= 0 ? expression.Substring(lastDot + 1) : expression;
		}

		private static void CollectDescendants(dynamic node, List<object> result)
		{
			foreach (object child in node.ChildNodes())
			{
				result.Add(child);
				CollectDescendants((dynamic)child, result);
			}
		}

		private static void CheckWaitCall(dynamic invocation, List<GuardrailViolation> violations)
		{
			dynamic expr = invocation.Expression;
			if (!IsKind(expr, "SimpleMemberAccessExpression"))
			{
				return;
			}

			string name = (string)expr.Name.Identifier.Text;
			if (name == "Wait")
			{
				AddViolation(violations, invocation, "blocking Wait() call");
			}
		}

		private static void CheckGetAwaiterGetResult(dynamic invocation, List<GuardrailViolation> violations)
		{
			dynamic expr = invocation.Expression;
			if (!IsKind(expr, "SimpleMemberAccessExpression"))
			{
				return;
			}

			string name = (string)expr.Name.Identifier.Text;
			if (name != "GetResult")
			{
				return;
			}

			dynamic inner = expr.Expression;
			if (!IsKind(inner, "InvocationExpression"))
			{
				return;
			}

			dynamic innerExpr = inner.Expression;
			if (!IsKind(innerExpr, "SimpleMemberAccessExpression"))
			{
				return;
			}

			string innerName = (string)innerExpr.Name.Identifier.Text;
			if (innerName == "GetAwaiter")
			{
				AddViolation(violations, invocation, "GetAwaiter().GetResult() blocks the main thread");
			}
		}

		private static void CheckThreadSleep(dynamic invocation, List<GuardrailViolation> violations)
		{
			dynamic expr = invocation.Expression;
			if (!IsKind(expr, "SimpleMemberAccessExpression"))
			{
				return;
			}

			string name = (string)expr.Name.Identifier.Text;
			if (name != "Sleep")
			{
				return;
			}

			string left = expr.Expression.ToString();
			if (left == "Thread" || left.EndsWith(".Thread", StringComparison.Ordinal) || left.EndsWith("Threading.Thread", StringComparison.Ordinal))
			{
				AddViolation(violations, invocation, "Thread.Sleep blocks the main thread");
			}
		}

		private static void CheckResultAccess(dynamic memberAccess, List<GuardrailViolation> violations)
		{
			string name = (string)memberAccess.Name.Identifier.Text;
			if (name != "Result")
			{
				return;
			}

			dynamic target = memberAccess.Expression;

			bool isInvocation = IsKind(target, "InvocationExpression");
			bool isTaskLikeIdentifier = false;

			if (IsKind(target, "IdentifierName"))
			{
				string identifierName = (string)target.Identifier.Text;
				isTaskLikeIdentifier = identifierName.EndsWith("Task", StringComparison.Ordinal) || identifierName.EndsWith("task", StringComparison.Ordinal);
			}

			if (isInvocation || isTaskLikeIdentifier)
			{
				AddViolation(violations, memberAccess, "blocking .Result access");
			}
		}

		private static void CheckWhileTrue(dynamic whileStatement, List<GuardrailViolation> violations)
		{
			dynamic condition = whileStatement.Condition;
			if (!IsKind(condition, "TrueLiteralExpression"))
			{
				return;
			}

			if (!ContainsAwait(whileStatement.Statement))
			{
				AddViolation(violations, whileStatement, "while (true) without await blocks the main thread");
			}
		}

		private static void CheckForEver(dynamic forStatement, List<GuardrailViolation> violations)
		{
			dynamic condition = forStatement.Condition;
			if (condition != null)
			{
				return;
			}

			if (!ContainsAwait(forStatement.Statement))
			{
				AddViolation(violations, forStatement, "for (;;) without await blocks the main thread");
			}
		}

		private static bool ContainsAwait(dynamic statement)
		{
			if (statement == null)
			{
				return false;
			}

			var nodes = new List<object>();
			nodes.Add((object)statement);
			CollectDescendants(statement, nodes);

			foreach (object nodeObj in nodes)
			{
				if (IsKind((dynamic)nodeObj, "AwaitExpression"))
				{
					return true;
				}
			}

			return false;
		}

		private static void AddViolation(List<GuardrailViolation> violations, dynamic node, string reason)
		{
			dynamic lineSpan = node.GetLocation().GetLineSpan();
			int line = (int)lineSpan.StartLinePosition.Line + 1;
			int column = (int)lineSpan.StartLinePosition.Character + 1;

			violations.Add(new GuardrailViolation
			{
				Reason = reason,
				Line = line,
				Column = column
			});
		}

		private static bool IsKind(dynamic node, string kindName)
		{
			if (node == null)
			{
				return false;
			}

			int raw = (int)node.RawKind;
			return raw == KindValue(kindName);
		}

		private static int KindValue(string name)
		{
			int value;
			if (_kindCache.TryGetValue(name, out value))
			{
				return value;
			}

			if (_syntaxKindType == null)
			{
				_syntaxKindType = RoslynResolver.CodeAnalysisCSharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.SyntaxKind");
			}

			object enumValue = Enum.Parse(_syntaxKindType, name);
			value = Convert.ToInt32(enumValue);
			_kindCache[name] = value;
			return value;
		}
	}
}

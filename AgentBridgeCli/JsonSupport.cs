using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentBridge.Cli;

internal static class JsonSupport
{
	public static JsonSerializerOptions Read { get; } = new()
	{
		PropertyNameCaseInsensitive = true
	};

	public static JsonSerializerOptions Output { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public static JsonSerializerOptions Task { get; } = new()
	{
		PropertyNamingPolicy = null,
		WriteIndented = false,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public static void Write(object value)
	{
		Console.Out.WriteLine(JsonSerializer.Serialize(value, Output));
	}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentBridge.Ui
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

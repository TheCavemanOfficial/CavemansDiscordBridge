using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// A small JSON reader for Discord's replies. Unity's JsonUtility isn't safe on the poller thread, and the game
    /// ships no general JSON library: objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double, plus string, bool and null.
    /// </summary>
    internal static class Json
    {
        internal static object Parse(string text)
        {
            int i = 0;
            object value = ReadValue(text, ref i);
            return value;
        }

        internal static string Str(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out object v) ? v as string : null;

        internal static bool Bool(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out object v) && v is bool b && b;

        internal static Dictionary<string, object> Obj(this Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;

        private static object ReadValue(string s, ref int i)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            switch (c)
            {
                case '{': return ReadObject(s, ref i);
                case '[': return ReadArray(s, ref i);
                case '"': return ReadString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ReadNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            var o = new Dictionary<string, object>();
            i++;   // {
            SkipSpace(s, ref i);
            if (s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipSpace(s, ref i);
                string key = ReadString(s, ref i);
                SkipSpace(s, ref i);
                if (s[i++] != ':') throw new FormatException("Expected ':' in JSON");
                o[key] = ReadValue(s, ref i);
                SkipSpace(s, ref i);
                char c = s[i++];
                if (c == '}') return o;
                if (c != ',') throw new FormatException("Expected ',' or '}' in JSON");
            }
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;   // [
            SkipSpace(s, ref i);
            if (s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i));
                SkipSpace(s, ref i);
                char c = s[i++];
                if (c == ']') return list;
                if (c != ',') throw new FormatException("Expected ',' or ']' in JSON");
            }
        }

        private static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected a string in JSON");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;   // \" \\ \/
                }
            }
        }

        private static double ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException($"Unexpected '{s[i]}' in JSON");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException($"Expected '{word}' in JSON");
            i += word.Length;
        }

        private static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AIUsageBar.Core
{
    /// <summary>
    /// Minimal JSON reader. Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double, plus string, bool and null.
    /// </summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("null input");
            var p = new Parser(text);
            p.SkipWs();
            var v = p.Value();
            p.SkipWs();
            if (!p.End) throw new FormatException("trailing characters at " + p.Pos);
            return v;
        }

        /// <summary>Dotted path lookup: "a.b.0.c". Numeric segments index arrays. Missing → null.</summary>
        public static object Get(object root, string path)
        {
            var cur = root;
            foreach (var seg in path.Split('.'))
            {
                if (cur is Dictionary<string, object> d)
                {
                    if (!d.TryGetValue(seg, out cur)) return null;
                }
                else if (cur is List<object> l && int.TryParse(seg, out var i))
                {
                    if (i < 0 || i >= l.Count) return null;
                    cur = l[i];
                }
                else return null;
            }
            return cur;
        }

        public static double? Num(object v)
        {
            switch (v)
            {
                case double d: return d;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r): return r;
                default: return null;
            }
        }

        public static string Str(object v)
        {
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case double d: return d.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                default: return null;
            }
        }

        private sealed class Parser
        {
            // Deeper input is rejected: unbounded recursion would end in an uncatchable StackOverflowException.
            private const int MaxDepth = 128;
            private readonly string _s;
            private int _depth;
            public int Pos;

            public Parser(string s) { _s = s; }

            public bool End => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length && char.IsWhiteSpace(_s[Pos])) Pos++;
            }

            private char Peek()
            {
                if (End) throw new FormatException("unexpected end of input");
                return _s[Pos];
            }

            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException($"expected '{c}' at {Pos}");
                Pos++;
            }

            public object Value()
            {
                SkipWs();
                var c = Peek();
                switch (c)
                {
                    case '{': return Nested(Obj);
                    case '[': return Nested(Arr);
                    case '"': return Str();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw new FormatException($"unexpected '{c}' at {Pos}");
                }
            }

            private object Nested(Func<object> parse)
            {
                if (++_depth > MaxDepth) throw new FormatException("nested too deeply at " + Pos);
                try { return parse(); }
                finally { _depth--; }
            }

            private void Word(string w)
            {
                if (string.CompareOrdinal(_s, Pos, w, 0, w.Length) != 0) throw new FormatException("bad literal at " + Pos);
                Pos += w.Length;
            }

            private Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>();
                Expect('{');
                SkipWs();
                if (Peek() == '}') { Pos++; return d; }
                while (true)
                {
                    SkipWs();
                    var k = Str();
                    SkipWs();
                    Expect(':');
                    d[k] = Value();
                    SkipWs();
                    if (Peek() == ',') { Pos++; continue; }
                    Expect('}');
                    return d;
                }
            }

            private List<object> Arr()
            {
                var l = new List<object>();
                Expect('[');
                SkipWs();
                if (Peek() == ']') { Pos++; return l; }
                while (true)
                {
                    l.Add(Value());
                    SkipWs();
                    if (Peek() == ',') { Pos++; continue; }
                    Expect(']');
                    return l;
                }
            }

            private string Str()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    var c = Peek();
                    Pos++;
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    var e = Peek();
                    Pos++;
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
                            if (Pos + 4 > _s.Length) throw new FormatException("bad unicode escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException("bad escape at " + Pos);
                    }
                }
            }

            private double Number()
            {
                var start = Pos;
                while (Pos < _s.Length && "+-0123456789.eE".IndexOf(_s[Pos]) >= 0) Pos++;
                if (!double.TryParse(_s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new FormatException("bad number at " + start);
                return d;
            }
        }
    }
}

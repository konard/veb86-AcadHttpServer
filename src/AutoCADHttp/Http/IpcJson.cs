using System;
using System.Collections.Generic;
using System.Text;

namespace AutoCADHttp.Http
{
    /// <summary>
    /// Reads bounded JSON envelopes without an external runtime DLL. Values retain their original JSON,
    /// so numbers and application payloads are forwarded without conversion or interpretation.
    /// </summary>
    internal sealed class IpcJson
    {
        public string Kind;
        public string String;
        public string Raw;
        public Dictionary<string, IpcJson> Members;

        public static IpcJson Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > LocalHttpServer.MaxBodyBytes)
                throw new FormatException("IPC JSON is empty or too large.");
            var reader = new Reader(json);
            IpcJson value = reader.ReadValue(0);
            reader.Space();
            if (reader.Position != json.Length)
                throw new FormatException("Trailing data after IPC JSON.");
            return value;
        }

        private sealed class Reader
        {
            private readonly string _json;
            public int Position;
            public Reader(string json) { _json = json; }
            private char Current { get { return Position < _json.Length ? _json[Position] : '\0'; } }

            public void Space()
            {
                while (Current == ' ' || Current == '\t' || Current == '\r' || Current == '\n') Position++;
            }

            private void Expect(char c)
            {
                Space();
                if (Current != c) throw Invalid();
                Position++;
            }

            public IpcJson ReadValue(int depth)
            {
                if (depth > 32) throw new FormatException("IPC JSON nesting exceeds 32.");
                Space();
                int start = Position;
                var value = new IpcJson();
                switch (Current)
                {
                    case '{':
                        value.Kind = "object";
                        value.Members = new Dictionary<string, IpcJson>(StringComparer.Ordinal);
                        Position++;
                        Space();
                        if (Current != '}')
                        {
                            while (true)
                            {
                                Space();
                                string key = ReadString();
                                Expect(':');
                                IpcJson member = ReadValue(depth + 1);
                                if (value.Members.ContainsKey(key)) throw new FormatException("Duplicate IPC JSON field: " + key);
                                value.Members.Add(key, member);
                                Space();
                                if (Current != ',') break;
                                Position++;
                            }
                        }
                        Expect('}');
                        break;
                    case '[':
                        value.Kind = "array";
                        Position++;
                        Space();
                        if (Current != ']')
                        {
                            while (true)
                            {
                                ReadValue(depth + 1);
                                Space();
                                if (Current != ',') break;
                                Position++;
                            }
                        }
                        Expect(']');
                        break;
                    case '"': value.Kind = "string"; value.String = ReadString(); break;
                    case 't': Literal("true"); value.Kind = "boolean"; break;
                    case 'f': Literal("false"); value.Kind = "boolean"; break;
                    case 'n': Literal("null"); value.Kind = "null"; break;
                    default: ReadNumber(); value.Kind = "number"; break;
                }
                value.Raw = _json.Substring(start, Position - start);
                return value;
            }

            private void Literal(string text)
            {
                foreach (char c in text)
                {
                    if (Current != c) throw Invalid();
                    Position++;
                }
            }

            private string ReadString()
            {
                if (Current != '"') throw Invalid();
                Position++;
                var result = new StringBuilder();
                while (Position < _json.Length)
                {
                    char c = _json[Position++];
                    if (c == '"') return result.ToString();
                    if (c < 0x20) throw Invalid();
                    if (c != '\\') { result.Append(c); continue; }
                    if (Position >= _json.Length) throw Invalid();
                    switch (_json[Position++])
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                char hex = Current;
                                int digit = hex >= '0' && hex <= '9' ? hex - '0' :
                                    hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 :
                                    hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                                if (digit < 0) throw Invalid();
                                code = code * 16 + digit;
                                Position++;
                            }
                            result.Append((char)code);
                            break;
                        default: throw Invalid();
                    }
                }
                throw Invalid();
            }

            private bool Digit { get { return Current >= '0' && Current <= '9'; } }
            private void Digits()
            {
                if (!Digit) throw Invalid();
                while (Digit) Position++;
            }

            private void ReadNumber()
            {
                if (Current == '-') Position++;
                if (Current == '0') Position++;
                else Digits();
                if (Current == '.') { Position++; Digits(); }
                if (Current == 'e' || Current == 'E')
                {
                    Position++;
                    if (Current == '+' || Current == '-') Position++;
                    Digits();
                }
            }

            private FormatException Invalid() { return new FormatException("Invalid IPC JSON at character " + Position + "."); }
        }
    }
}

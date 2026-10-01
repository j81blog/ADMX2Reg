using System.Globalization;
using System.Text;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class RegFileReader {
    public static partial IReadOnlyList<RegistryOperation> Read(TextReader reader, List<string> warnings) {
        var state = new ParseState(warnings);
        var lineNumber = 0;
        var firstLine = true;
        string? raw;
        while ((raw = reader.ReadLine()) != null) {
            lineNumber++;
            var line = raw.TrimStart('﻿').Trim();
            if (firstLine && line.Length > 0) {
                firstLine = false;
                if (line.StartsWith("REGEDIT4", StringComparison.OrdinalIgnoreCase)) {
                    state.Ansi = true;
                    continue;
                }
                if (line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                warnings.Add($"Line {lineNumber}: missing .reg header, parsing anyway.");
            }
            if (line.Length == 0 || line[0] == ';') {
                continue;
            }

            // Join "\" continuation lines (hex data).
            var startLine = lineNumber;
            while (line.EndsWith('\\') && line[0] != '[') {
                var next = reader.ReadLine();
                if (next == null) {
                    line = line[..^1];
                    break;
                }
                lineNumber++;
                line = line[..^1] + next.Trim();
            }

            if (line.Length > 0 && line[0] == '[') {
                state.Header(line, startLine);
            } else {
                state.Value(line, startLine);
            }
        }
        state.Finish();
        return state.Result;
    }

    private sealed class ParseState(List<string> warnings) {
        public readonly List<RegistryOperation> Result = [];
        public bool Ansi;

        private RegistryHive? _hive;
        private string _key = "";
        private bool _inKey;          // values go to a supported key
        private bool _hasValues;      // current [key] had at least one value line
        private bool _recreated;      // current [key] followed a matching [-key]
        private (RegistryHive Hive, string Key)? _pendingDelete;

        public void Header(string line, int lineNumber) {
            EndKey();
            var end = line.LastIndexOf(']');
            if (end < 0) {
                warnings.Add($"Line {lineNumber}: malformed key header, skipped.");
                FlushPendingDelete();
                return;
            }
            var path = line[1..end].Trim();
            var delete = path.StartsWith('-');
            if (delete) {
                path = path[1..];
            }
            if (!TryParsePath(path, out var hive, out var key)) {
                warnings.Add($"Line {lineNumber}: unsupported registry hive in '{path}', skipped.");
                FlushPendingDelete();
                return;
            }

            if (delete) {
                FlushPendingDelete();
                _pendingDelete = (hive, key);
                return;
            }

            if (_pendingDelete is { } pending && pending.Hive == hive && string.Equals(pending.Key, key, StringComparison.OrdinalIgnoreCase)) {
                Result.Add(RegistryOperation.DeleteAllValues(hive, key));
                _recreated = true;
                _pendingDelete = null;
            } else {
                FlushPendingDelete();
            }
            _hive = hive;
            _key = key;
            _inKey = true;
            _hasValues = false;
        }

        public void Value(string line, int lineNumber) {
            if (!_inKey || _hive == null) {
                warnings.Add($"Line {lineNumber}: value outside a supported key, skipped.");
                return;
            }
            var pos = 0;
            string name;
            if (line[0] == '@') {
                name = "";
                pos = 1;
            } else if (line[0] == '"') {
                name = ReadQuoted(line, ref pos, out var closed);
                if (!closed) {
                    warnings.Add($"Line {lineNumber}: unterminated value name, skipped.");
                    return;
                }
            } else {
                warnings.Add($"Line {lineNumber}: unrecognized line, skipped.");
                return;
            }
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) {
                pos++;
            }
            if (pos >= line.Length || line[pos] != '=') {
                warnings.Add($"Line {lineNumber}: expected '=' after value name, skipped.");
                return;
            }
            var data = line[(pos + 1)..].Trim();
            _hasValues = true;

            if (data == "-") {
                Result.Add(RegistryOperation.DeleteValue(_hive.Value, _key, name));
                return;
            }
            try {
                Result.Add(ParseData(_hive.Value, _key, name, data, lineNumber));
            } catch (FormatException ex) {
                warnings.Add($"Line {lineNumber}: {ex.Message}");
            }
        }

        public void Finish() {
            EndKey();
            FlushPendingDelete();
        }

        private void EndKey() {
            if (_inKey && _hive != null && !_hasValues && !_recreated) {
                Result.Add(new RegistryOperation { Hive = _hive.Value, Kind = RegistryOperationKind.CreateKey, Key = _key });
            }
            _inKey = false;
            _recreated = false;
        }

        private void FlushPendingDelete() {
            if (_pendingDelete is { } p) {
                Result.Add(RegistryOperation.DeleteKey(p.Hive, p.Key));
                _pendingDelete = null;
            }
        }

        private RegistryOperation ParseData(RegistryHive hive, string key, string name, string data, int lineNumber) {
            if (data.StartsWith('"')) {
                var pos = 0;
                var s = ReadQuoted(data, ref pos, out var closed);
                if (!closed) {
                    throw new FormatException("unterminated string value, skipped.");
                }
                return RegistryOperation.Set(hive, key, name, RegValueType.String, s);
            }
            if (data.StartsWith("dword:", StringComparison.OrdinalIgnoreCase)) {
                if (!uint.TryParse(data[6..].Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var d)) {
                    throw new FormatException("invalid dword value, skipped.");
                }
                return RegistryOperation.Set(hive, key, name, RegValueType.DWord, d);
            }
            if (data.StartsWith("hex", StringComparison.OrdinalIgnoreCase)) {
                var colon = data.IndexOf(':');
                if (colon < 0) {
                    throw new FormatException("invalid hex value, skipped.");
                }
                var kindText = data[3..colon].Trim();
                var typeCode = 3;
                if (kindText.Length > 0) {
                    if (!kindText.StartsWith('(') || !kindText.EndsWith(')')
                        || !int.TryParse(kindText[1..^1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out typeCode)) {
                        throw new FormatException("invalid hex type, skipped.");
                    }
                }
                var bytes = ParseHexBytes(data[(colon + 1)..]);
                return ToOperation(hive, key, name, typeCode, bytes, lineNumber);
            }
            throw new FormatException("unrecognized value data, skipped.");
        }

        private RegistryOperation ToOperation(RegistryHive hive, string key, string name, int typeCode, byte[] bytes, int lineNumber) {
            switch (typeCode) {
                case 0:
                    return RegistryOperation.Set(hive, key, name, RegValueType.None, null);
                case 1:
                    return RegistryOperation.Set(hive, key, name, RegValueType.String, DecodeString(bytes));
                case 2:
                    return RegistryOperation.Set(hive, key, name, RegValueType.ExpandString, DecodeString(bytes));
                case 3:
                    return RegistryOperation.Set(hive, key, name, RegValueType.Binary, bytes);
                case 4:
                    if (bytes.Length == 4) {
                        return RegistryOperation.Set(hive, key, name, RegValueType.DWord, BitConverter.ToUInt32(bytes, 0));
                    }
                    break;
                case 7:
                    return RegistryOperation.Set(hive, key, name, RegValueType.MultiString, DecodeMulti(bytes));
                case 0xb:
                    if (bytes.Length == 8) {
                        return RegistryOperation.Set(hive, key, name, RegValueType.QWord, BitConverter.ToUInt64(bytes, 0));
                    }
                    break;
            }
            warnings.Add($"Line {lineNumber}: value '{name}' has unsupported or malformed type hex({typeCode:x}), imported as binary.");
            return RegistryOperation.Set(hive, key, name, RegValueType.Binary, bytes);
        }

        private string Decode(byte[] bytes) =>
            Ansi ? Encoding.Latin1.GetString(bytes) : Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);

        private string DecodeString(byte[] bytes) {
            var text = Decode(bytes);
            var nul = text.IndexOf('\0');
            return nul >= 0 ? text[..nul] : text;
        }

        private string[] DecodeMulti(byte[] bytes) {
            var items = Decode(bytes).Split('\0').ToList();
            while (items.Count > 0 && items[^1].Length == 0) {
                items.RemoveAt(items.Count - 1);
            }
            return items.ToArray();
        }
    }

    private static byte[] ParseHexBytes(string text) {
        var bytes = new List<byte>();
        foreach (var part in text.Split(',')) {
            var p = part.Trim();
            if (p.Length == 0) {
                continue;
            }
            if (!byte.TryParse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b)) {
                throw new FormatException($"invalid hex byte '{p}', skipped.");
            }
            bytes.Add(b);
        }
        return bytes.ToArray();
    }

    /// <summary>Reads a quoted string starting at <paramref name="pos"/> (which points at the opening quote).</summary>
    private static string ReadQuoted(string s, ref int pos, out bool closed) {
        var sb = new StringBuilder();
        pos++;
        closed = false;
        while (pos < s.Length) {
            var c = s[pos++];
            if (c == '\\' && pos < s.Length) {
                sb.Append(s[pos++]);
            } else if (c == '"') {
                closed = true;
                break;
            } else {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool TryParsePath(string path, out RegistryHive hive, out string key) {
        hive = RegistryHive.LocalMachine;
        key = "";
        var slash = path.IndexOf('\\');
        var root = slash < 0 ? path : path[..slash];
        var rest = slash < 0 ? "" : path[(slash + 1)..].Trim('\\');
        if (root.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) || root.Equals("HKLM", StringComparison.OrdinalIgnoreCase)) {
            hive = RegistryHive.LocalMachine;
        } else if (root.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) || root.Equals("HKCU", StringComparison.OrdinalIgnoreCase)) {
            hive = RegistryHive.CurrentUser;
        } else {
            return false;
        }
        key = rest;
        return true;
    }
}

using System.Globalization;
using System.Text;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class RegFileWriter {
    private const string NewLine = "\r\n";
    private const int MaxColumns = 80;

    /// <remarks>
    /// A .reg file has no "delete all values but keep subkeys" statement. DeleteAllValues is written as
    /// [-key] followed immediately by [key], which recreates the key empty; this also removes all subkeys.
    /// <see cref="RegFileReader"/> turns that pair back into DeleteAllValues.
    /// </remarks>
    public static partial void Write(IEnumerable<RegistryOperation> operations, TextWriter writer, string? headerComment) {
        writer.Write("Windows Registry Editor Version 5.00" + NewLine + NewLine);
        if (!string.IsNullOrEmpty(headerComment)) {
            foreach (var line in headerComment.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)) {
                writer.Write(line.Length == 0 ? ";" : "; " + line);
                writer.Write(NewLine);
            }
            writer.Write(NewLine);
        }

        string? openKey = null;
        foreach (var op in operations) {
            var fullKey = FullKey(op);
            switch (op.Kind) {
                case RegistryOperationKind.DeleteKey:
                    StartSection(writer, ref openKey, null);
                    writer.Write("[-" + fullKey + "]" + NewLine);
                    break;
                case RegistryOperationKind.DeleteAllValues:
                    StartSection(writer, ref openKey, null);
                    writer.Write("; The .reg format cannot delete only the values of a key. The key is deleted and recreated empty, which also removes its subkeys." + NewLine);
                    writer.Write("[-" + fullKey + "]" + NewLine);
                    writer.Write("[" + fullKey + "]" + NewLine);
                    openKey = fullKey;
                    break;
                case RegistryOperationKind.CreateKey:
                    OpenKey(writer, ref openKey, fullKey);
                    break;
                case RegistryOperationKind.DeleteValue:
                    OpenKey(writer, ref openKey, fullKey);
                    writer.Write(Name(op.ValueName) + "=-" + NewLine);
                    break;
                case RegistryOperationKind.SetValue:
                    OpenKey(writer, ref openKey, fullKey);
                    WriteValue(writer, op);
                    break;
            }
        }
    }

    private static string FullKey(RegistryOperation op) {
        var hive = op.Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        return string.IsNullOrEmpty(op.Key) ? hive : hive + "\\" + op.Key;
    }

    private static void StartSection(TextWriter writer, ref string? openKey, string? newKey) {
        if (openKey != null) {
            writer.Write(NewLine);
        }
        openKey = newKey;
    }

    private static void OpenKey(TextWriter writer, ref string? openKey, string fullKey) {
        if (openKey != null && string.Equals(openKey, fullKey, StringComparison.OrdinalIgnoreCase)) {
            return;
        }
        if (openKey != null) {
            writer.Write(NewLine);
        }
        writer.Write("[" + fullKey + "]" + NewLine);
        openKey = fullKey;
    }

    private static string Name(string? name) => string.IsNullOrEmpty(name) ? "@" : "\"" + Escape(name) + "\"";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void WriteValue(TextWriter writer, RegistryOperation op) {
        var prefix = Name(op.ValueName) + "=";
        switch (op.Type) {
            case RegValueType.String: {
                var s = op.Data as string ?? "";
                if (s.Contains('\r') || s.Contains('\n')) {
                    WriteHex(writer, prefix + "hex(1):", Utf16WithTerminator(s));
                } else {
                    writer.Write(prefix + "\"" + Escape(s) + "\"" + NewLine);
                }
                break;
            }
            case RegValueType.ExpandString:
                WriteHex(writer, prefix + "hex(2):", Utf16WithTerminator(op.Data as string ?? ""));
                break;
            case RegValueType.DWord:
                writer.Write(prefix + "dword:" + Convert.ToUInt32(op.Data, CultureInfo.InvariantCulture).ToString("x8", CultureInfo.InvariantCulture) + NewLine);
                break;
            case RegValueType.QWord:
                WriteHex(writer, prefix + "hex(b):", BitConverter.GetBytes(Convert.ToUInt64(op.Data, CultureInfo.InvariantCulture)));
                break;
            case RegValueType.MultiString: {
                var bytes = new List<byte>();
                foreach (var s in op.Data as string[] ?? []) {
                    bytes.AddRange(Encoding.Unicode.GetBytes(s));
                    bytes.AddRange([0, 0]);
                }
                bytes.AddRange([0, 0]);
                WriteHex(writer, prefix + "hex(7):", bytes.ToArray());
                break;
            }
            case RegValueType.Binary:
                WriteHex(writer, prefix + "hex:", op.Data as byte[] ?? []);
                break;
            default:
                WriteHex(writer, prefix + "hex(0):", []);
                break;
        }
    }

    private static byte[] Utf16WithTerminator(string s) {
        var bytes = new byte[Encoding.Unicode.GetByteCount(s) + 2];
        Encoding.Unicode.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    /// <summary>Hex data wrapped like regedit: lines end with ",\" and continue after a 2 space indent.</summary>
    private static void WriteHex(TextWriter writer, string prefix, byte[] data) {
        var line = new StringBuilder(prefix);
        for (var i = 0; i < data.Length; i++) {
            var token = data[i].ToString("x2", CultureInfo.InvariantCulture);
            var last = i == data.Length - 1;
            if (!last) {
                token += ",";
            }
            // Reserve one column for the trailing backslash.
            if (line.Length + token.Length + 1 > MaxColumns && line.Length > 2) {
                writer.Write(line + "\\" + NewLine);
                line.Clear().Append("  ");
            }
            line.Append(token);
        }
        writer.Write(line + NewLine);
    }
}

using System.Globalization;
using System.Text;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class PolFileWriter {
    private const uint Signature = 0x67655250; // "PReg"
    private const uint Version = 1;

    private const uint RegNone = 0;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const uint RegBinary = 3;
    private const uint RegDword = 4;
    private const uint RegMultiSz = 7;
    private const uint RegQword = 11;

    /// <remarks>
    /// Format (Microsoft Learn, "Registry Policy File Format"): header (signature 0x67655250, version 1), then
    /// [key;value;type;size;data] entries, every character UTF-16LE with null terminated key and value fields.
    /// Deletes follow Group Policy: DeleteValue is "**del.name", DeleteAllValues is "**delvals." (both REG_SZ with
    /// data " "), DeleteKey is a "**DeleteKeys" value on the parent key whose data is the subkey name.
    /// A key without a parent gets an empty key field (best effort, Group Policy never deletes a root key).
    /// </remarks>
    public static partial void Write(IEnumerable<RegistryOperation> operations, Stream stream) {
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(Signature);
        writer.Write(Version);

        foreach (var op in operations) {
            switch (op.Kind) {
                case RegistryOperationKind.SetValue:
                    WriteSet(writer, op);
                    break;
                case RegistryOperationKind.DeleteValue:
                    WriteEntry(writer, op.Key, "**del." + op.ValueName, RegSz, Utf16Z(" "));
                    break;
                case RegistryOperationKind.DeleteAllValues:
                    WriteEntry(writer, op.Key, "**delvals.", RegSz, Utf16Z(" "));
                    break;
                case RegistryOperationKind.DeleteKey: {
                    var slash = op.Key.LastIndexOf('\\');
                    var parent = slash < 0 ? "" : op.Key[..slash];
                    var child = slash < 0 ? op.Key : op.Key[(slash + 1)..];
                    WriteEntry(writer, parent, "**DeleteKeys", RegSz, Utf16Z(child));
                    break;
                }
                case RegistryOperationKind.CreateKey:
                    WriteEntry(writer, op.Key, "", RegNone, []);
                    break;
            }
        }
        writer.Flush();
    }

    private static void WriteSet(BinaryWriter writer, RegistryOperation op) {
        var name = op.ValueName ?? "";
        switch (op.Type) {
            case RegValueType.String:
                WriteEntry(writer, op.Key, name, RegSz, Utf16Z(op.Data as string ?? ""));
                break;
            case RegValueType.ExpandString:
                WriteEntry(writer, op.Key, name, RegExpandSz, Utf16Z(op.Data as string ?? ""));
                break;
            case RegValueType.DWord:
                WriteEntry(writer, op.Key, name, RegDword, BitConverter.GetBytes(Convert.ToUInt32(op.Data, CultureInfo.InvariantCulture)));
                break;
            case RegValueType.QWord:
                WriteEntry(writer, op.Key, name, RegQword, BitConverter.GetBytes(Convert.ToUInt64(op.Data, CultureInfo.InvariantCulture)));
                break;
            case RegValueType.MultiString: {
                var bytes = new List<byte>();
                foreach (var s in op.Data as string[] ?? []) {
                    bytes.AddRange(Encoding.Unicode.GetBytes(s));
                    bytes.AddRange([0, 0]);
                }
                bytes.AddRange([0, 0]);
                WriteEntry(writer, op.Key, name, RegMultiSz, bytes.ToArray());
                break;
            }
            case RegValueType.Binary:
                WriteEntry(writer, op.Key, name, RegBinary, op.Data as byte[] ?? []);
                break;
            default:
                WriteEntry(writer, op.Key, name, RegNone, []);
                break;
        }
    }

    private static byte[] Utf16Z(string s) {
        var bytes = new byte[Encoding.Unicode.GetByteCount(s) + 2];
        Encoding.Unicode.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    private static void WriteEntry(BinaryWriter writer, string key, string valueName, uint type, byte[] data) {
        WriteChar(writer, '[');
        WriteString(writer, key);
        WriteChar(writer, ';');
        WriteString(writer, valueName);
        WriteChar(writer, ';');
        writer.Write(type);
        WriteChar(writer, ';');
        writer.Write((uint)data.Length);
        WriteChar(writer, ';');
        writer.Write(data);
        WriteChar(writer, ']');
    }

    private static void WriteChar(BinaryWriter writer, char c) => writer.Write((ushort)c);

    /// <summary>String as UTF-16LE followed by a null terminator.</summary>
    private static void WriteString(BinaryWriter writer, string s) {
        writer.Write(Encoding.Unicode.GetBytes(s));
        writer.Write((ushort)0);
    }
}

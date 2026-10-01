using System.Text;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class PolFileReader {
    public static partial IReadOnlyList<RegistryOperation> Read(Stream stream, RegistryHive hive) {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var data = copy.ToArray();

        if (data.Length < 8) {
            throw new InvalidDataException("The Registry.pol file is too short to contain a header.");
        }
        if (BitConverter.ToUInt32(data, 0) != 0x67655250) {
            throw new InvalidDataException("The file is not a Registry.pol file (missing 'PReg' signature).");
        }
        var version = BitConverter.ToUInt32(data, 4);
        if (version != 1) {
            throw new InvalidDataException($"Unsupported Registry.pol version {version}.");
        }

        var result = new List<RegistryOperation>();
        var pos = 8;
        while (pos < data.Length) {
            var entryStart = pos;
            Expect(data, ref pos, '[', entryStart);
            var key = ReadString(data, ref pos, entryStart);
            Expect(data, ref pos, ';', entryStart);
            var valueName = ReadString(data, ref pos, entryStart);
            Expect(data, ref pos, ';', entryStart);
            var type = ReadUInt32(data, ref pos, entryStart);
            Expect(data, ref pos, ';', entryStart);
            var size = ReadUInt32(data, ref pos, entryStart);
            Expect(data, ref pos, ';', entryStart);
            if (size > data.Length - pos) {
                throw Truncated(entryStart);
            }
            var bytes = data.AsSpan(pos, (int)size).ToArray();
            pos += (int)size;
            Expect(data, ref pos, ']', entryStart);

            AddOperations(result, hive, key.Trim('\\'), valueName, type, bytes);
        }
        return result;
    }

    private static void AddOperations(List<RegistryOperation> result, RegistryHive hive, string key, string valueName, uint type, byte[] bytes) {
        if (valueName.StartsWith("**del.", StringComparison.OrdinalIgnoreCase)) {
            result.Add(RegistryOperation.DeleteValue(hive, key, valueName["**del.".Length..]));
        } else if (valueName.Equals("**delvals.", StringComparison.OrdinalIgnoreCase) || valueName.Equals("**delvals", StringComparison.OrdinalIgnoreCase)) {
            result.Add(RegistryOperation.DeleteAllValues(hive, key));
        } else if (valueName.Equals("**DeleteValues", StringComparison.OrdinalIgnoreCase)) {
            foreach (var name in SplitList(bytes)) {
                result.Add(RegistryOperation.DeleteValue(hive, key, name));
            }
        } else if (valueName.Equals("**DeleteKeys", StringComparison.OrdinalIgnoreCase)) {
            foreach (var sub in SplitList(bytes)) {
                result.Add(RegistryOperation.DeleteKey(hive, key.Length == 0 ? sub : key + "\\" + sub));
            }
        } else if (valueName.StartsWith("**SecureKey", StringComparison.OrdinalIgnoreCase)) {
            // Access control hint for Group Policy, not a registry value.
        } else if (valueName.Length == 0 && bytes.Length == 0) {
            result.Add(new RegistryOperation { Hive = hive, Kind = RegistryOperationKind.CreateKey, Key = key });
        } else {
            result.Add(ToSetValue(hive, key, valueName, type, bytes));
        }
    }

    private static RegistryOperation ToSetValue(RegistryHive hive, string key, string name, uint type, byte[] bytes) {
        switch (type) {
            case 0:
                return RegistryOperation.Set(hive, key, name, RegValueType.None, null);
            case 1:
                return RegistryOperation.Set(hive, key, name, RegValueType.String, DecodeString(bytes));
            case 2:
                return RegistryOperation.Set(hive, key, name, RegValueType.ExpandString, DecodeString(bytes));
            case 4:
                return RegistryOperation.Set(hive, key, name, RegValueType.DWord, ReadNumber32(bytes, bigEndian: false));
            case 5:
                return RegistryOperation.Set(hive, key, name, RegValueType.DWord, ReadNumber32(bytes, bigEndian: true));
            case 7: {
                var items = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).Split('\0').ToList();
                while (items.Count > 0 && items[^1].Length == 0) {
                    items.RemoveAt(items.Count - 1);
                }
                return RegistryOperation.Set(hive, key, name, RegValueType.MultiString, items.ToArray());
            }
            case 11: {
                var padded = new byte[8];
                Array.Copy(bytes, padded, Math.Min(8, bytes.Length));
                return RegistryOperation.Set(hive, key, name, RegValueType.QWord, BitConverter.ToUInt64(padded, 0));
            }
            default: // REG_BINARY, REG_LINK and unknown types are kept as raw bytes.
                return RegistryOperation.Set(hive, key, name, RegValueType.Binary, bytes);
        }
    }

    private static uint ReadNumber32(byte[] bytes, bool bigEndian) {
        var padded = new byte[4];
        Array.Copy(bytes, padded, Math.Min(4, bytes.Length));
        if (bigEndian) {
            Array.Reverse(padded);
        }
        return BitConverter.ToUInt32(padded, 0);
    }

    private static string DecodeString(byte[] bytes) {
        var text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    /// <summary>Semicolon delimited list in the data of **DeleteValues / **DeleteKeys.</summary>
    private static IEnumerable<string> SplitList(byte[] bytes) =>
        DecodeString(bytes).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static InvalidDataException Truncated(int entryStart) =>
        new($"The Registry.pol file is truncated or corrupt in the entry starting at byte offset {entryStart}.");

    private static void Expect(byte[] data, ref int pos, char expected, int entryStart) {
        if (pos + 2 > data.Length) {
            throw Truncated(entryStart);
        }
        if (BitConverter.ToUInt16(data, pos) != expected) {
            throw new InvalidDataException($"The Registry.pol file is corrupt: expected '{expected}' at byte offset {pos}.");
        }
        pos += 2;
    }

    private static uint ReadUInt32(byte[] data, ref int pos, int entryStart) {
        if (pos + 4 > data.Length) {
            throw Truncated(entryStart);
        }
        var v = BitConverter.ToUInt32(data, pos);
        pos += 4;
        return v;
    }

    /// <summary>Reads UTF-16LE characters up to and including the null terminator.</summary>
    private static string ReadString(byte[] data, ref int pos, int entryStart) {
        var start = pos;
        while (true) {
            if (pos + 2 > data.Length) {
                throw Truncated(entryStart);
            }
            if (data[pos] == 0 && data[pos + 1] == 0) {
                var s = Encoding.Unicode.GetString(data, start, pos - start);
                pos += 2;
                return s;
            }
            pos += 2;
        }
    }
}

using System.Text;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core.Tests;

public class PolFileTests {
    private const RegistryHive Hklm = RegistryHive.LocalMachine;

    private static byte[] Write(IEnumerable<RegistryOperation> ops) {
        using var ms = new MemoryStream();
        PolFileWriter.Write(ops, ms);
        return ms.ToArray();
    }

    private static List<RegistryOperation> Read(byte[] data, RegistryHive hive = Hklm) =>
        PolFileReader.Read(new MemoryStream(data), hive).ToList();

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    private static byte[] Entry(string key, string value, uint type, byte[] data) {
        var bytes = new List<byte>();
        bytes.AddRange(Utf16("[" + key + "\0;" + value + "\0;"));
        bytes.AddRange(BitConverter.GetBytes(type));
        bytes.AddRange(Utf16(";"));
        bytes.AddRange(BitConverter.GetBytes((uint)data.Length));
        bytes.AddRange(Utf16(";"));
        bytes.AddRange(data);
        bytes.AddRange(Utf16("]"));
        return bytes.ToArray();
    }

    private static byte[] File_(params byte[][] entries) =>
        BitConverter.GetBytes(0x67655250u).Concat(BitConverter.GetBytes(1u)).Concat(entries.SelectMany(e => e)).ToArray();

    [Fact]
    public void Writer_EmitsHeaderAndExactEntryLayout() {
        var bytes = Write([RegistryOperation.Set(Hklm, @"Software\P", "V", RegValueType.DWord, 1u)]);
        var expected = File_(Entry(@"Software\P", "V", 4, BitConverter.GetBytes(1u)));
        Assert.Equal(expected, bytes);
        Assert.Equal("PReg", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Writer_DeleteFormats() {
        var space = Utf16(" \0");
        var bytes = Write([
            RegistryOperation.DeleteValue(Hklm, @"Software\P", "Name"),
            RegistryOperation.DeleteAllValues(Hklm, @"Software\P"),
            RegistryOperation.DeleteKey(Hklm, @"Software\P\Sub"),
            RegistryOperation.DeleteKey(Hklm, "Root"),
            new RegistryOperation { Hive = Hklm, Kind = RegistryOperationKind.CreateKey, Key = @"Software\Made" }
        ]);
        var expected = File_(
            Entry(@"Software\P", "**del.Name", 1, space),
            Entry(@"Software\P", "**delvals.", 1, space),
            Entry(@"Software\P", "**DeleteKeys", 1, Utf16("Sub\0")),
            Entry("", "**DeleteKeys", 1, Utf16("Root\0")),
            Entry(@"Software\Made", "", 0, []));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void RoundTrip_AllTypesAndKinds() {
        List<RegistryOperation> ops = [
            RegistryOperation.Set(Hklm, @"Software\P", "S", RegValueType.String, "str é日"),
            RegistryOperation.Set(Hklm, @"Software\P", "E", RegValueType.ExpandString, "%TEMP%\\x"),
            RegistryOperation.Set(Hklm, @"Software\P", "D", RegValueType.DWord, uint.MaxValue),
            RegistryOperation.Set(Hklm, @"Software\P", "Q", RegValueType.QWord, ulong.MaxValue),
            RegistryOperation.Set(Hklm, @"Software\P", "B", RegValueType.Binary, new byte[] { 0, 1, 2 }),
            RegistryOperation.Set(Hklm, @"Software\P", "M", RegValueType.MultiString, new[] { "a", "b c" }),
            RegistryOperation.Set(Hklm, @"Software\P", "ME", RegValueType.MultiString, Array.Empty<string>()),
            RegistryOperation.Set(Hklm, @"Software\P", "N", RegValueType.None, null),
            RegistryOperation.Set(Hklm, @"Software\P", "", RegValueType.String, "default"),
            RegistryOperation.DeleteValue(Hklm, @"Software\P", "Gone"),
            RegistryOperation.DeleteAllValues(Hklm, @"Software\List"),
            RegistryOperation.DeleteKey(Hklm, @"Software\P\Sub"),
            new RegistryOperation { Hive = Hklm, Kind = RegistryOperationKind.CreateKey, Key = @"Software\Made" }
        ];

        var read = Read(Write(ops), RegistryHive.CurrentUser);
        Assert.Equal(ops.Count, read.Count);
        for (var i = 0; i < ops.Count; i++) {
            Assert.Equal(RegistryHive.CurrentUser, read[i].Hive);
            Assert.Equal(ops[i].Kind, read[i].Kind);
            Assert.Equal(ops[i].Key, read[i].Key);
            Assert.Equal(ops[i].ValueName ?? "", read[i].ValueName ?? "");
            Assert.Equal(ops[i].Type, read[i].Type);
            Assert.Equal(ops[i].Data, read[i].Data);
        }
    }

    [Fact]
    public void Reader_ExpandsDeleteValuesAndDeleteKeysListsAndIgnoresSecureKey() {
        var data = File_(
            Entry(@"Software\P", "**DeleteValues", 1, Utf16("One;Two\0")),
            Entry(@"Software\P", "**DeleteKeys", 1, Utf16("A;B\0")),
            Entry("", "**DeleteKeys", 1, Utf16("TopLevel\0")),
            Entry(@"Software\P", "**SecureKey", 4, BitConverter.GetBytes(1u)),
            Entry(@"Software\P", "**DELVALS", 1, Utf16(" \0")),
            Entry(@"Software\P", "**Del.Case", 1, Utf16(" \0")));
        var ops = Read(data);

        Assert.Equal(
            ["DeleteValue:Software\\P:One", "DeleteValue:Software\\P:Two", "DeleteKey:Software\\P\\A:", "DeleteKey:Software\\P\\B:",
             "DeleteKey:TopLevel:", "DeleteAllValues:Software\\P:", "DeleteValue:Software\\P:Case"],
            ops.Select(o => $"{o.Kind}:{o.Key}:{o.ValueName}").ToArray());
    }

    [Fact]
    public void Reader_EmptyValueNameWithoutDataIsCreateKey() {
        var ops = Read(File_(Entry(@"Software\P", "", 0, [])));
        Assert.Single(ops);
        Assert.Equal(RegistryOperationKind.CreateKey, ops[0].Kind);
    }

    [Fact]
    public void Reader_AcceptsHeaderOnlyFile() {
        Assert.Empty(Read(File_()));
    }

    [Fact]
    public void Reader_ThrowsClearErrorsOnBadInput() {
        var good = File_(Entry(@"Software\P", "V", 4, BitConverter.GetBytes(1u)));
        Assert.Throws<InvalidDataException>(() => Read([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => Read(Enumerable.Repeat((byte)0x41, 16).ToArray()));
        for (var len = 9; len < good.Length; len++) {
            var cut = good[..len];
            var ex = Assert.Throws<InvalidDataException>(() => Read(cut));
            Assert.Contains("Registry.pol", ex.Message);
        }
    }
}

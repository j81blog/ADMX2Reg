using System.Diagnostics;
using System.Text;
using ADMX2Reg.Core.Registry;
using Reg = Microsoft.Win32.Registry;
using RegistryValueKind = Microsoft.Win32.RegistryValueKind;
using RegistryValueOptions = Microsoft.Win32.RegistryValueOptions;

namespace ADMX2Reg.Core.Tests;

public class RegFileTests {
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;

    private static string Write(IEnumerable<RegistryOperation> ops, string? header = null) {
        var sw = new StringWriter();
        RegFileWriter.Write(ops, sw, header);
        return sw.ToString();
    }

    private static List<RegistryOperation> Read(string text, out List<string> warnings) {
        warnings = [];
        return RegFileReader.Read(new StringReader(text), warnings).ToList();
    }

    [Fact]
    public void Writer_ProducesRegeditFormat() {
        var text = Write([
            RegistryOperation.Set(Hklm, @"Software\Policies\Test", "Str", RegValueType.String, "a \"quoted\" C:\\path"),
            RegistryOperation.Set(Hklm, @"Software\Policies\Test", "Dw", RegValueType.DWord, 255u),
            RegistryOperation.Set(Hklm, @"Software\Policies\Test", "", RegValueType.String, "default"),
            RegistryOperation.Set(Hkcu, @"Software\Other", "Q", RegValueType.QWord, 0x0102030405060708UL),
            RegistryOperation.DeleteValue(Hkcu, @"Software\Other", "Gone"),
            RegistryOperation.DeleteKey(Hkcu, @"Software\Dead")
        ], "Line one\nLine two");

        var expected =
            "Windows Registry Editor Version 5.00\r\n\r\n" +
            "; Line one\r\n; Line two\r\n\r\n" +
            "[HKEY_LOCAL_MACHINE\\Software\\Policies\\Test]\r\n" +
            "\"Str\"=\"a \\\"quoted\\\" C:\\\\path\"\r\n" +
            "\"Dw\"=dword:000000ff\r\n" +
            "@=\"default\"\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\Other]\r\n" +
            "\"Q\"=hex(b):08,07,06,05,04,03,02,01\r\n" +
            "\"Gone\"=-\r\n\r\n" +
            "[-HKEY_CURRENT_USER\\Software\\Dead]\r\n";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Writer_WrapsLongHexLikeRegedit() {
        var text = Write([RegistryOperation.Set(Hkcu, "K", "Bin", RegValueType.Binary, Enumerable.Range(0, 60).Select(i => (byte)i).ToArray())]);
        var lines = text.Split("\r\n").Where(l => l.Length > 0).Skip(2).ToArray();
        Assert.StartsWith("\"Bin\"=hex:00,01", lines[0]);
        Assert.EndsWith(",\\", lines[0]);
        Assert.All(lines, l => Assert.True(l.Length <= 80, l));
        Assert.StartsWith("  ", lines[1]);
        Assert.DoesNotContain("\\", lines[^1]);
    }

    [Fact]
    public void Writer_DeleteAllValuesWritesDeleteKeyThenRecreate() {
        var text = Write([RegistryOperation.DeleteAllValues(Hklm, @"Software\Foo"), RegistryOperation.Set(Hklm, @"Software\Foo", "A", RegValueType.DWord, 1u)]);
        Assert.Contains("; ", text);
        Assert.Contains("[-HKEY_LOCAL_MACHINE\\Software\\Foo]\r\n[HKEY_LOCAL_MACHINE\\Software\\Foo]\r\n\"A\"=dword:00000001", text);
    }

    public static IEnumerable<object[]> RoundTripCases() {
        var ops = new List<RegistryOperation> {
            RegistryOperation.Set(Hklm, @"Software\T", "Str", RegValueType.String, "plain"),
            RegistryOperation.Set(Hklm, @"Software\T", "Esc", RegValueType.String, "back\\slash \"quote\" 'apos' \u00e9\u65e5"),
            RegistryOperation.Set(Hklm, @"Software\T", "Lines", RegValueType.String, "a\r\nb"),
            RegistryOperation.Set(Hklm, @"Software\T", "", RegValueType.String, "default"),
            RegistryOperation.Set(Hklm, @"Software\T", "Expand", RegValueType.ExpandString, "%SystemRoot%\\x"),
            RegistryOperation.Set(Hklm, @"Software\T", "D0", RegValueType.DWord, 0u),
            RegistryOperation.Set(Hklm, @"Software\T", "DMax", RegValueType.DWord, uint.MaxValue),
            RegistryOperation.Set(Hklm, @"Software\T", "QMax", RegValueType.QWord, ulong.MaxValue),
            RegistryOperation.Set(Hklm, @"Software\T", "Bin", RegValueType.Binary, new byte[] { 1, 2, 3, 255 }),
            RegistryOperation.Set(Hklm, @"Software\T", "BinLong", RegValueType.Binary, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray()),
            RegistryOperation.Set(Hklm, @"Software\T", "BinEmpty", RegValueType.Binary, Array.Empty<byte>()),
            RegistryOperation.Set(Hklm, @"Software\T", "Multi", RegValueType.MultiString, new[] { "one", "two words", "\u00fc" }),
            RegistryOperation.Set(Hklm, @"Software\T", "MultiEmpty", RegValueType.MultiString, Array.Empty<string>()),
            RegistryOperation.Set(Hklm, @"Software\T", "None", RegValueType.None, null),
            RegistryOperation.DeleteValue(Hklm, @"Software\T", "Gone"),
            RegistryOperation.DeleteAllValues(Hkcu, @"Software\List"),
            RegistryOperation.Set(Hkcu, @"Software\List", "1", RegValueType.String, "x"),
            RegistryOperation.DeleteAllValues(Hkcu, @"Software\Other"),
            RegistryOperation.DeleteKey(Hkcu, @"Software\Dead"),
            new RegistryOperation { Hive = Hkcu, Kind = RegistryOperationKind.CreateKey, Key = @"Software\Empty" },
            RegistryOperation.Set(Hkcu, @"Software\Empty2", "V", RegValueType.DWord, 5u)
        };
        yield return [ops];
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrip_WriterThenReaderRestoresOperations(List<RegistryOperation> ops) {
        var read = Read(Write(ops), out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(ops.Count, read.Count);
        for (var i = 0; i < ops.Count; i++) {
            AssertSame(ops[i], read[i]);
        }
    }

    private static void AssertSame(RegistryOperation expected, RegistryOperation actual) {
        Assert.Equal(expected.Hive, actual.Hive);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Key, actual.Key);
        if (expected.Kind is RegistryOperationKind.SetValue or RegistryOperationKind.DeleteValue) {
            Assert.Equal(expected.ValueName ?? "", actual.ValueName);
        }
        if (expected.Kind == RegistryOperationKind.SetValue) {
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Data, actual.Data);
        }
    }

    [Fact]
    public void Reader_ParsesRegedit4AsAnsi() {
        var text = "REGEDIT4\r\n\r\n[HKEY_LOCAL_MACHINE\\Software\\Old]\r\n\"S\"=\"caf\u00e9\"\r\n\"M\"=hex(7):61,00,62,00,00\r\n";
        var ops = Read(text, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal("caf\u00e9", ops[0].Data);
        Assert.Equal(new[] { "a", "b" }, (string[])ops[1].Data!);
    }

    [Fact]
    public void Reader_HandlesCommentsContinuationsAndHiveAliases() {
        var text = string.Join("\r\n",
            "Windows Registry Editor Version 5.00",
            "",
            "; a comment",
            "[hkcu\\Software\\T]",
            "\"B\"=hex:01,02,\\",
            "  03,04",
            "; inside",
            "\"D\"=dword:0000000A",
            "",
            "[HKEY_CLASSES_ROOT\\Foo]",
            "\"X\"=\"y\"",
            "[HKLM\\Software\\Z]",
            "\"N\"=-",
            "");
        var ops = Read(text, out var warnings);
        Assert.Equal(3, ops.Count);
        Assert.Equal(Hkcu, ops[0].Hive);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, ops[0].Data);
        Assert.Equal(10u, ops[1].Data);
        Assert.Equal(RegistryOperationKind.DeleteValue, ops[2].Kind);
        Assert.Contains(warnings, w => w.Contains("HKEY_CLASSES_ROOT"));
        Assert.Contains(warnings, w => w.Contains("value outside"));
    }

    [Fact]
    public void Reader_BadLinesProduceWarningsNotExceptions() {
        var ops = Read("Windows Registry Editor Version 5.00\r\n[HKLM\\K]\r\n\"A\"=dword:zz\r\n\"B\"=hex:xx\r\nnonsense\r\n\"C\"=dword:00000001\r\n", out var warnings);
        Assert.Single(ops);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RealRegImport_AcceptsWriterOutput() {
        var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(reg)) {
            return;
        }

        const string root = @"Software\ADMX2RegTest\RegImport";
        var file = Path.Combine(Path.GetTempPath(), "a2r_import.reg");
        try {
            Reg.CurrentUser.DeleteSubKeyTree(root, false);
            var ops = new List<RegistryOperation> {
                RegistryOperation.Set(Hkcu, root + @"\A", "Str", RegValueType.String, "a \"q\" C:\\p"),
                RegistryOperation.Set(Hkcu, root + @"\A", "Lines", RegValueType.String, "a\r\nb"),
                RegistryOperation.Set(Hkcu, root + @"\A", "", RegValueType.String, "def"),
                RegistryOperation.Set(Hkcu, root + @"\A", "Exp", RegValueType.ExpandString, "%TEMP%\\x"),
                RegistryOperation.Set(Hkcu, root + @"\A", "D", RegValueType.DWord, uint.MaxValue),
                RegistryOperation.Set(Hkcu, root + @"\A", "Q", RegValueType.QWord, ulong.MaxValue),
                RegistryOperation.Set(Hkcu, root + @"\A", "Bin", RegValueType.Binary, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()),
                RegistryOperation.Set(Hkcu, root + @"\A", "Multi", RegValueType.MultiString, new[] { "x", "y z" }),
                RegistryOperation.Set(Hkcu, root + @"\A", "None", RegValueType.None, null),
                RegistryOperation.DeleteAllValues(Hkcu, root + @"\A"),
                RegistryOperation.Set(Hkcu, root + @"\A", "After", RegValueType.DWord, 1u),
                new RegistryOperation { Hive = Hkcu, Kind = RegistryOperationKind.CreateKey, Key = root + @"\Created" }
            };
            File.WriteAllText(file, Write(ops.Take(9).Append(ops[11])), new UnicodeEncoding(false, true));

            var psi = new ProcessStartInfo(reg, $"import \"{file}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)!) {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, output);
            }

            using var a = Reg.CurrentUser.OpenSubKey(root + @"\A")!;
            Assert.Equal("a \"q\" C:\\p", a.GetValue("Str"));
            Assert.Equal("a\r\nb", a.GetValue("Lines"));
            Assert.Equal("def", a.GetValue(""));
            Assert.Equal(RegistryValueKind.ExpandString, a.GetValueKind("Exp"));
            Assert.Equal("%TEMP%\\x", a.GetValue("Exp", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(-1, (int)a.GetValue("D")!);
            Assert.Equal(-1L, (long)a.GetValue("Q")!);
            Assert.Equal(100, ((byte[])a.GetValue("Bin")!).Length);
            Assert.Equal(new[] { "x", "y z" }, (string[])a.GetValue("Multi")!);
            Assert.Equal(RegistryValueKind.None, a.GetValueKind("None"));
            Assert.NotNull(Reg.CurrentUser.OpenSubKey(root + @"\Created"));

            // The delete-all pair removes every earlier value.
            File.WriteAllText(file, Write([
                RegistryOperation.DeleteAllValues(Hkcu, root + @"\A"),
                RegistryOperation.Set(Hkcu, root + @"\A", "After", RegValueType.DWord, 1u)
            ]), new UnicodeEncoding(false, true));
            psi = new ProcessStartInfo(reg, $"import \"{file}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)!) {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            using var a2 = Reg.CurrentUser.OpenSubKey(root + @"\A")!;
            Assert.Equal(new[] { "After" }, a2.GetValueNames());
        } finally {
            Reg.CurrentUser.DeleteSubKeyTree(root, false);
            using var top = Reg.CurrentUser.OpenSubKey(@"Software\ADMX2RegTest");
            if (top is { SubKeyCount: 0, ValueCount: 0 }) {
                top.Dispose();
                Reg.CurrentUser.DeleteSubKey(@"Software\ADMX2RegTest", false);
            }
            File.Delete(file);
        }
    }
}

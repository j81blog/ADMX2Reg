using System.Diagnostics;
using System.Text;
using ADMX2Reg.Core.Registry;
using Reg = Microsoft.Win32.Registry;
using RegistryValueKind = Microsoft.Win32.RegistryValueKind;
using RegistryValueOptions = Microsoft.Win32.RegistryValueOptions;

namespace ADMX2Reg.Core.Tests;

/// <summary>
/// Generates scripts and, when the shell is available, really runs them against HKCU\Software\ADMX2RegTest.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class PowerShellWriterTests {
    private const string TestRoot = @"Software\ADMX2RegTest";

    private static string Generate(IEnumerable<RegistryOperation> ops, PowerShellStyle style, string? header = null) {
        var sw = new StringWriter();
        PowerShellWriter.Write(ops, sw, style, header);
        return sw.ToString();
    }

    private static RegistryOperation Set(string key, string name, RegValueType type, object? data) =>
        RegistryOperation.Set(RegistryHive.CurrentUser, key, name, type, data);

    [Fact]
    public void Minimal_UsesLiteralPathAndGuardsNewItem() {
        var script = Generate([Set(@"Software\Test", "A", RegValueType.DWord, 1u)], PowerShellStyle.Minimal, "my header");
        Assert.Contains("# my header", script);
        Assert.Contains("if (-not (Test-Path -LiteralPath 'HKCU:\\Software\\Test')) {", script);
        Assert.Contains("New-Item -Path 'HKCU:\\Software\\Test' -Force | Out-Null", script);
        Assert.Contains("New-ItemProperty -LiteralPath 'HKCU:\\Software\\Test' -Name 'A' -PropertyType DWord -Value 1 -Force | Out-Null", script);
        Assert.DoesNotContain("-Path 'HKCU:\\Software\\Test' -Name", script);
    }

    [Fact]
    public void Standalone_AdminCheckOnlyWhenHklmPresent() {
        var hkcu = Generate([Set(@"Software\Test", "A", RegValueType.DWord, 1u)], PowerShellStyle.Standalone);
        var hklm = Generate([RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\Test", "A", RegValueType.DWord, 1u)], PowerShellStyle.Standalone);
        Assert.DoesNotContain("IsInRole", hkcu);
        Assert.Contains("IsInRole", hklm);
        Assert.Contains("SupportsShouldProcess = $true", hklm);
        Assert.Contains(".SYNOPSIS", hklm);
        Assert.Contains("'HKLM:\\Software\\Test'", hklm);
    }

    [Fact]
    public void Strings_AreEscaped() {
        var script = Generate([Set("Software\\It's", "na'me", RegValueType.String, "it's \"q\" \u2019x\u2019 \r\n")], PowerShellStyle.Minimal);
        Assert.Contains("'HKCU:\\Software\\It''s'", script);
        Assert.Contains("'na''me'", script);
        Assert.Contains("[char]0x2019", script);
        Assert.Contains("[char]0x000d", script);
    }

    private static List<RegistryOperation> FinalStateOps() => [
        Set(@"Sub", "Str", RegValueType.String, "it's a \"test\" C:\\path\\ \u00e9 \u00fc \u65e5\u672c \u2018smart\u2019 $notvar `tick"),
        Set(@"Sub", "Multiline", RegValueType.String, "line1\r\nline2"),
        Set(@"Sub", "Expand", RegValueType.ExpandString, "%SystemRoot%\\system32"),
        Set(@"Sub", "DwordMax", RegValueType.DWord, uint.MaxValue),
        Set(@"Sub", "DwordZero", RegValueType.DWord, 0u),
        Set(@"Sub", "QwordMax", RegValueType.QWord, ulong.MaxValue),
        Set(@"Sub", "QwordBig", RegValueType.QWord, 5000000000UL),
        Set(@"Sub", "Bin", RegValueType.Binary, new byte[] { 0, 1, 0xff }),
        Set(@"Sub", "BinEmpty", RegValueType.Binary, Array.Empty<byte>()),
        Set(@"Sub", "Multi", RegValueType.MultiString, new[] { "a", "b'c", "d e" }),
        Set(@"Sub", "MultiEmpty", RegValueType.MultiString, Array.Empty<string>()),
        Set(@"Sub", "NoneValue", RegValueType.None, null),
        Set(@"Sub", "", RegValueType.String, "default value"),
        Set(@"Sub", "we*ird[name]?'", RegValueType.String, "wild"),
        Set(@"Wild\k*[x]?", "V", RegValueType.DWord, 7u),
        RegistryOperation.DeleteValue(RegistryHive.CurrentUser, @"Pre", "Gone"),
        RegistryOperation.DeleteValue(RegistryHive.CurrentUser, @"Pre", "AlreadyAbsent"),
        RegistryOperation.DeleteAllValues(RegistryHive.CurrentUser, @"ClearKey"),
        RegistryOperation.DeleteKey(RegistryHive.CurrentUser, @"DelKey"),
        new() { Hive = RegistryHive.CurrentUser, Kind = RegistryOperationKind.CreateKey, Key = @"Created\Deep" }
    ];

    private static List<RegistryOperation> WithBase(List<RegistryOperation> ops, string baseKey) =>
        ops.Select(o => o with { Key = baseKey + "\\" + o.Key }).ToList();

    private static void PreCreate(string baseKey) {
        using var pre = Reg.CurrentUser.CreateSubKey(baseKey + @"\Pre");
        pre.SetValue("Gone", "x");
        pre.SetValue("Keep", "y");
        using var clear = Reg.CurrentUser.CreateSubKey(baseKey + @"\ClearKey");
        clear.SetValue("One", 1);
        clear.SetValue("", "default");
        using var sub = Reg.CurrentUser.CreateSubKey(baseKey + @"\ClearKey\SubKeep");
        sub.SetValue("S", 1);
        using var del = Reg.CurrentUser.CreateSubKey(baseKey + @"\DelKey\Child");
        del.SetValue("C", 1);
    }

    private static (int ExitCode, string Output) Run(string exe, string scriptPath, string extraArgs = "") {
        var psi = new ProcessStartInfo(exe, $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {extraArgs}") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static string? FindShell(string name) {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }
        if (name == "powershell.exe") {
            var path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(path) ? path : null;
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwsh = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh) ? pwsh : null;
    }

    private static void AssertFinalState(string baseKey) {
        using var sub = Reg.CurrentUser.OpenSubKey(baseKey + @"\Sub")!;
        Assert.NotNull(sub);
        Assert.Equal("it's a \"test\" C:\\path\\ \u00e9 \u00fc \u65e5\u672c \u2018smart\u2019 $notvar `tick", sub.GetValue("Str"));
        Assert.Equal("line1\r\nline2", sub.GetValue("Multiline"));
        Assert.Equal(RegistryValueKind.ExpandString, sub.GetValueKind("Expand"));
        Assert.Equal("%SystemRoot%\\system32", sub.GetValue("Expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.DWord, sub.GetValueKind("DwordMax"));
        Assert.Equal(uint.MaxValue, unchecked((uint)(int)sub.GetValue("DwordMax")!));
        Assert.Equal(0, (int)sub.GetValue("DwordZero")!);
        Assert.Equal(RegistryValueKind.QWord, sub.GetValueKind("QwordMax"));
        Assert.Equal(ulong.MaxValue, unchecked((ulong)(long)sub.GetValue("QwordMax")!));
        Assert.Equal(5000000000L, (long)sub.GetValue("QwordBig")!);
        Assert.Equal(new byte[] { 0, 1, 0xff }, (byte[])sub.GetValue("Bin")!);
        Assert.Equal(RegistryValueKind.Binary, sub.GetValueKind("BinEmpty"));
        Assert.Equal(new[] { "a", "b'c", "d e" }, (string[])sub.GetValue("Multi")!);
        Assert.Equal(RegistryValueKind.MultiString, sub.GetValueKind("MultiEmpty"));
        Assert.Empty((string[])sub.GetValue("MultiEmpty")!);
        Assert.Equal(RegistryValueKind.None, sub.GetValueKind("NoneValue"));
        Assert.Equal("default value", sub.GetValue(""));
        Assert.Equal("wild", sub.GetValue("we*ird[name]?'"));

        using var wild = Reg.CurrentUser.OpenSubKey(baseKey + @"\Wild\k*[x]?")!;
        Assert.NotNull(wild);
        Assert.Equal(7, (int)wild.GetValue("V")!);

        using var pre = Reg.CurrentUser.OpenSubKey(baseKey + @"\Pre")!;
        Assert.Null(pre.GetValue("Gone"));
        Assert.Equal("y", pre.GetValue("Keep"));

        using var clear = Reg.CurrentUser.OpenSubKey(baseKey + @"\ClearKey")!;
        Assert.Empty(clear.GetValueNames());
        Assert.NotNull(clear.OpenSubKey("SubKeep"));

        Assert.Null(Reg.CurrentUser.OpenSubKey(baseKey + @"\DelKey"));
        Assert.NotNull(Reg.CurrentUser.OpenSubKey(baseKey + @"\Created\Deep"));
    }

    private static void Cleanup(string baseKey) {
        Reg.CurrentUser.DeleteSubKeyTree(baseKey, throwOnMissingSubKey: false);
        using var root = Reg.CurrentUser.OpenSubKey(TestRoot);
        if (root != null && root.SubKeyCount == 0 && root.ValueCount == 0) {
            root.Dispose();
            Reg.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData("powershell.exe", PowerShellStyle.Minimal)]
    [InlineData("powershell.exe", PowerShellStyle.Standalone)]
    [InlineData("pwsh.exe", PowerShellStyle.Minimal)]
    [InlineData("pwsh.exe", PowerShellStyle.Standalone)]
    public void RealRun_AppliesEveryKindAndTypeCorrectly(string shell, PowerShellStyle style) {
        var exe = FindShell(shell);
        if (exe == null) {
            return; // shell not installed on this machine
        }

        var baseKey = $@"{TestRoot}\Run_{shell.Replace(".exe", "")}_{style}";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"a2r_{shell}_{style}.ps1");
        try {
            Cleanup(baseKey);
            PreCreate(baseKey);
            var script = Generate(WithBase(FinalStateOps(), baseKey), style, "header line");
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            if (Environment.GetEnvironmentVariable("ADMX2REG_SCRIPT_DUMP") is { Length: > 0 } dump) {
                File.WriteAllText(Path.Combine(dump, $"{shell}_{style}.ps1"), script, new UTF8Encoding(true));
            }

            var first = Run(exe, scriptPath);
            Assert.True(first.ExitCode == 0, first.Output);
            AssertFinalState(baseKey);

            var second = Run(exe, scriptPath);
            Assert.True(second.ExitCode == 0, second.Output);
            AssertFinalState(baseKey);
            if (style == PowerShellStyle.Standalone) {
                Assert.Contains("Changed: 0,", second.Output);
                Assert.Contains("Failed: 0.", second.Output);
            }
        } finally {
            Cleanup(baseKey);
            File.Delete(scriptPath);
        }
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void RealRun_StandaloneWhatIfChangesNothing(string shell) {
        var exe = FindShell(shell);
        if (exe == null) {
            return;
        }

        var baseKey = $@"{TestRoot}\WhatIf_{shell.Replace(".exe", "")}";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"a2r_whatif_{shell}.ps1");
        try {
            Cleanup(baseKey);
            PreCreate(baseKey);
            File.WriteAllText(scriptPath, Generate(WithBase(FinalStateOps(), baseKey), PowerShellStyle.Standalone), new UTF8Encoding(true));

            var result = Run(exe, scriptPath, "-WhatIf");
            Assert.True(result.ExitCode == 0, result.Output);

            Assert.Null(Reg.CurrentUser.OpenSubKey(baseKey + @"\Sub"));
            Assert.Null(Reg.CurrentUser.OpenSubKey(baseKey + @"\Created"));
            using var pre = Reg.CurrentUser.OpenSubKey(baseKey + @"\Pre")!;
            Assert.Equal("x", pre.GetValue("Gone"));
            using var clear = Reg.CurrentUser.OpenSubKey(baseKey + @"\ClearKey")!;
            Assert.Equal(2, clear.ValueCount);
            Assert.NotNull(Reg.CurrentUser.OpenSubKey(baseKey + @"\DelKey\Child"));
        } finally {
            Cleanup(baseKey);
            File.Delete(scriptPath);
        }
    }
}

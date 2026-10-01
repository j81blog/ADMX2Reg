using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core.Tests;

public sealed class GpoStoreTests : IDisposable {
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "admx2reg-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_folder)) {
            Directory.Delete(_folder, true);
        }
    }

    private static GpoDocument Sample() {
        var gpo = new GpoDocument { Name = "Sample", Description = "desc" };
        var s = new PolicySetting { PolicyId = "Ns:P", State = PolicyState.Enabled, Comment = "c" };
        s.Values["Num"] = new PolicyElementValue { Number = ulong.MaxValue };
        s.Values["Flag"] = new PolicyElementValue { Boolean = false };
        s.Values["Txt"] = new PolicyElementValue { Text = "t" };
        s.Values["Multi"] = new PolicyElementValue { Lines = ["a", "b"] };
        s.Values["Enum"] = new PolicyElementValue { EnumIndex = 2 };
        s.Values["List"] = new PolicyElementValue { Entries = [new ListEntry { Name = "n", Value = "v" }] };
        gpo.Computer.Add(s);
        gpo.User.Add(new PolicySetting { PolicyId = "Ns:Q", State = PolicyState.Disabled });
        gpo.ComputerExtraRegistry.AddRange([
            RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\X", "s", RegValueType.String, "text"),
            RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\X", "e", RegValueType.ExpandString, "%TEMP%"),
            RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\X", "d", RegValueType.DWord, uint.MaxValue),
            RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\X", "q", RegValueType.QWord, ulong.MaxValue),
            RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\X", "m", RegValueType.MultiString, new[] { "one", "two" }),
            RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\X", "b", RegValueType.Binary, new byte[] { 0, 1, 255 }),
            RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\X", "n", RegValueType.None, null),
            RegistryOperation.DeleteValue(RegistryHive.LocalMachine, @"Software\X", "gone"),
            RegistryOperation.DeleteAllValues(RegistryHive.LocalMachine, @"Software\Y"),
            RegistryOperation.DeleteKey(RegistryHive.LocalMachine, @"Software\Z"),
            new RegistryOperation { Hive = RegistryHive.LocalMachine, Kind = RegistryOperationKind.CreateKey, Key = @"Software\New" }
        ]);
        gpo.UserExtraRegistry.Add(RegistryOperation.Set(RegistryHive.CurrentUser, @"Software\U", "v", RegValueType.DWord, 5u));
        return gpo;
    }

    private static void AssertOpsEqual(IReadOnlyList<RegistryOperation> expected, IReadOnlyList<RegistryOperation> actual) {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) {
            var e = expected[i];
            var a = actual[i];
            Assert.Equal(e.Hive, a.Hive);
            Assert.Equal(e.Kind, a.Kind);
            Assert.Equal(e.Key, a.Key);
            Assert.Equal(e.ValueName, a.ValueName);
            Assert.Equal(e.Type, a.Type);
            if (e.Data == null) {
                Assert.Null(a.Data);
            } else {
                Assert.IsType(e.Data.GetType(), a.Data);
                if (e.Data is Array ea) {
                    Assert.Equal(ea.Cast<object>(), ((Array)a.Data!).Cast<object>());
                } else {
                    Assert.Equal(e.Data, a.Data);
                }
            }
        }
    }

    [Fact]
    public void Serialize_RoundTripsEverything() {
        var gpo = Sample();
        var json = GpoStore.Serialize(gpo);
        Assert.Contains("\"state\": \"Enabled\"", json);
        Assert.Contains("\"computer\"", json);
        var back = GpoStore.Deserialize(json);

        Assert.Equal(gpo.Id, back.Id);
        Assert.Equal("Sample", back.Name);
        Assert.Equal(gpo.Created, back.Created);
        var s = back.Computer.Single();
        Assert.Equal(PolicyState.Enabled, s.State);
        Assert.Equal("c", s.Comment);
        Assert.Equal(ulong.MaxValue, s.Values["Num"].Number);
        Assert.Equal(false, s.Values["Flag"].Boolean);
        Assert.Equal("t", s.Values["Txt"].Text);
        Assert.Equal(["a", "b"], s.Values["Multi"].Lines);
        Assert.Equal(2, s.Values["Enum"].EnumIndex);
        Assert.Equal("v", s.Values["List"].Entries![0].Value);
        Assert.True(s.Values.ContainsKey("num"), "element lookup should be case-insensitive");
        Assert.Equal(PolicyState.Disabled, back.User.Single().State);
        AssertOpsEqual(gpo.ComputerExtraRegistry, back.ComputerExtraRegistry);
        AssertOpsEqual(gpo.UserExtraRegistry, back.UserExtraRegistry);
    }

    [Fact]
    public void SaveAndLoadAll_RoundTripAndSetFilePath() {
        var store = new GpoStore(_folder);
        var gpo = Sample();
        store.Save(gpo);
        Assert.Equal(Path.Combine(_folder, gpo.Id + ".json"), gpo.FilePath);
        Assert.True(File.Exists(gpo.FilePath));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));

        var loaded = new GpoStore(_folder).LoadAll(out var errors);
        Assert.Empty(errors);
        var one = Assert.Single(loaded);
        Assert.Equal(gpo.FilePath, one.FilePath);
        AssertOpsEqual(gpo.ComputerExtraRegistry, one.ComputerExtraRegistry);

        // Saving again overwrites the same file.
        one.Name = "Renamed";
        store.Save(one);
        Assert.Equal("Renamed", Assert.Single(store.LoadAll(out _)).Name);
    }

    [Fact]
    public void LoadAll_CreatesFolderAndSkipsCorruptFiles() {
        var store = new GpoStore(_folder);
        Assert.Empty(store.LoadAll(out var none));
        Assert.Empty(none);
        Assert.True(Directory.Exists(_folder));

        File.WriteAllText(Path.Combine(_folder, "bad.json"), "{ not json");
        store.Save(Sample());
        var loaded = store.LoadAll(out var errors);
        Assert.Single(loaded);
        Assert.Contains("bad.json", Assert.Single(errors));
    }

    [Fact]
    public void Delete_RemovesFile() {
        var store = new GpoStore(_folder);
        var gpo = Sample();
        store.Save(gpo);
        store.Delete(gpo);
        Assert.False(File.Exists(gpo.FilePath));
        store.Delete(gpo);
    }

    [Fact]
    public void Duplicate_DeepCopyWithNewIdentity() {
        var gpo = Sample();
        gpo.FilePath = @"C:\x.json";
        var copy = GpoStore.Duplicate(gpo);
        Assert.NotEqual(gpo.Id, copy.Id);
        Assert.Equal("Sample (Copy)", copy.Name);
        Assert.Null(copy.FilePath);
        Assert.NotSame(gpo.Computer[0], copy.Computer[0]);
        copy.Computer[0].Values["Txt"].Text = "changed";
        Assert.Equal("t", gpo.Computer[0].Values["Txt"].Text);
        AssertOpsEqual(gpo.ComputerExtraRegistry, copy.ComputerExtraRegistry);
    }
}

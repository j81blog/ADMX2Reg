using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core.Tests;

public class PolicyMatcherTests {
    private const RegistryHive Hklm = RegistryHive.LocalMachine;
    private const RegistryHive Hkcu = RegistryHive.CurrentUser;
    private const string K = @"Software\Policies\Test";

    private static PolicyValue Dec(ulong n) => new(PolicyValueKind.Decimal, n);
    private static PolicyValue Str(string s) => new(PolicyValueKind.String, 0, s);
    private static PolicyValue Del() => new(PolicyValueKind.Delete);

    private static PolicyDefinition Policy(string name, string key, string? valueName, PolicyClass cls = PolicyClass.Machine,
        PolicyValue? enabled = null, PolicyValue? disabled = null, PolicyValueList? enabledList = null, PolicyValueList? disabledList = null,
        params PolicyElement[] elements) {
        var p = new PolicyDefinition {
            Namespace = "Test",
            Name = name,
            DisplayName = name,
            SourceFile = "t.admx",
            Class = cls,
            Key = key,
            ValueName = valueName,
            EnabledValue = enabled,
            DisabledValue = disabled,
            EnabledList = enabledList,
            DisabledList = disabledList
        };
        p.Elements.AddRange(elements);
        return p;
    }

    private static AdmxCatalog Catalog(params PolicyDefinition[] policies) {
        var catalog = new AdmxCatalog { SourcePath = "", Language = "en-US" };
        foreach (var p in policies) {
            catalog.Policies[p.Id] = p;
        }
        return catalog;
    }

    private static PolicyValueList VList(string? defaultKey, params (string? Key, string Name, PolicyValue Value)[] items) {
        var list = new PolicyValueList { DefaultKey = defaultKey };
        list.Items.AddRange(items.Select(i => new PolicyListItem(i.Key, i.Name, i.Value)));
        return list;
    }

    private static RegistryOperation Set(string key, string name, RegValueType type, object? data, RegistryHive hive = Hklm) =>
        RegistryOperation.Set(hive, key, name, type, data);

    // ------------------------------------------------------------------ simple state

    [Fact]
    public void EnabledAndDisabledValuesAreRecognized() {
        var p = Policy("P", K, "Flag", enabled: Dec(5), disabled: Dec(9));
        var catalog = Catalog(p);

        var on = PolicyMatcher.Match([Set(K, "Flag", RegValueType.DWord, 5u)], catalog);
        Assert.Equal(PolicyState.Enabled, Assert.Single(on.Computer).State);
        Assert.Empty(on.UnmatchedComputer);

        var off = PolicyMatcher.Match([Set(K, "Flag", RegValueType.DWord, 9u)], catalog);
        Assert.Equal(PolicyState.Disabled, Assert.Single(off.Computer).State);

        var other = PolicyMatcher.Match([Set(K, "Flag", RegValueType.DWord, 7u)], catalog);
        Assert.Empty(other.Computer);
        Assert.Single(other.UnmatchedComputer);
    }

    [Fact]
    public void DefaultMarkersAreDwordOneAndDeleteValue() {
        var catalog = Catalog(Policy("P", K, "Flag"));
        var on = PolicyMatcher.Match([Set(K.ToUpperInvariant(), "FLAG", RegValueType.DWord, 1u)], catalog);
        Assert.Equal(PolicyState.Enabled, Assert.Single(on.Computer).State);

        var off = PolicyMatcher.Match([RegistryOperation.DeleteValue(Hklm, K, "Flag")], catalog);
        Assert.Equal(PolicyState.Disabled, Assert.Single(off.Computer).State);

        // DWORD 2 is not the default enabled value.
        Assert.Empty(PolicyMatcher.Match([Set(K, "Flag", RegValueType.DWord, 2u)], catalog).Computer);
    }

    [Fact]
    public void DeleteEnabledValueAndStringMarkers() {
        var catalog = Catalog(
            Policy("Del", K, "D", enabled: Del(), disabled: Str("off")),
            Policy("Text", K, "T", enabled: Str("on"), disabled: Del()));
        var result = PolicyMatcher.Match([
            RegistryOperation.DeleteValue(Hklm, K, "D"),
            Set(K, "T", RegValueType.String, "on")
        ], catalog);
        Assert.Equal(2, result.Computer.Count);
        Assert.All(result.Computer, s => Assert.Equal(PolicyState.Enabled, s.State));
        Assert.Empty(result.UnmatchedComputer);
    }

    [Fact]
    public void EnabledListAndDisabledListAreMatched() {
        var p = Policy("L", K, null,
            enabledList: VList(K, (null, "A", Dec(1)), (@"Software\Other", "B", Str("x"))),
            disabledList: VList(K, (null, "A", Dec(0)), (@"Software\Other", "B", Str("y"))));
        var catalog = Catalog(p);

        var on = PolicyMatcher.Match([Set(K, "A", RegValueType.DWord, 1u), Set(@"Software\Other", "B", RegValueType.String, "x")], catalog);
        Assert.Equal(PolicyState.Enabled, Assert.Single(on.Computer).State);
        Assert.Empty(on.UnmatchedComputer);

        var off = PolicyMatcher.Match([Set(K, "A", RegValueType.DWord, 0u), Set(@"Software\Other", "B", RegValueType.String, "y")], catalog);
        Assert.Equal(PolicyState.Disabled, Assert.Single(off.Computer).State);

        // Only half of the enabled list present: not a match.
        var partial = PolicyMatcher.Match([Set(K, "A", RegValueType.DWord, 1u)], catalog);
        Assert.Empty(partial.Computer);
        Assert.Single(partial.UnmatchedComputer);
    }

    // ------------------------------------------------------------------ elements

    private static PolicyDefinition BigPolicy() {
        var enumItems = new EnumElement { Id = "Mode", ValueName = "Mode" };
        enumItems.Items.Add(new EnumItem { DisplayName = "Zero", Value = Dec(0) });
        enumItems.Items.Add(new EnumItem { DisplayName = "One", Value = Dec(1), ValueList = VList(null, (null, "ModeExtra", Str("one"))) });
        enumItems.Items.Add(new EnumItem { DisplayName = "Two", Value = Dec(1), ValueList = VList(null, (null, "ModeExtra", Str("two"))) });
        enumItems.Items.Add(new EnumItem { DisplayName = "Text", Value = Str("custom") });

        return Policy("Big", K, "Big", enabled: Dec(1), disabled: Dec(0), elements: [
            new DecimalElement { Id = "Num", ValueName = "Num" },
            new DecimalElement { Id = "NumText", ValueName = "NumText", StoreAsText = true },
            new LongDecimalElement { Id = "Long", ValueName = "Long" },
            new TextElement { Id = "Txt", ValueName = "Txt" },
            new TextElement { Id = "Exp", ValueName = "Exp", Expandable = true },
            new MultiTextElement { Id = "Multi", ValueName = "Multi" },
            new BooleanElement { Id = "BoolDefault", ValueName = "BoolDefault" },
            new BooleanElement { Id = "BoolCustom", ValueName = "BoolCustom", TrueValue = Str("yes"), FalseValue = Str("no") },
            new BooleanElement {
                Id = "BoolList",
                TrueList = VList(null, (null, "BL1", Dec(1)), (null, "BL2", Dec(1))),
                FalseList = VList(null, (null, "BL1", Dec(0)), (null, "BL2", Dec(0)))
            },
            new BooleanElement { Id = "BoolOverrideKey", Key = @"Software\Policies\Test\Sub", ValueName = "Sub" },
            enumItems,
            new ListElement { Id = "Prefixed", Key = K + @"\Prefixed", ValuePrefix = "Item" },
            new ListElement { Id = "Numbered", Key = K + @"\Numbered", ValuePrefix = "" },
            new ListElement { Id = "Explicit", Key = K + @"\Explicit", ExplicitValue = true },
            new ListElement { Id = "NameIsValue", Key = K + @"\NameIsValue" },
            new ListElement { Id = "Additive", Key = K + @"\Additive", Additive = true, ValuePrefix = "A" }
        ]);
    }

    private static List<RegistryOperation> BigEnabledOps() => [
        Set(K, "Big", RegValueType.DWord, 1u),
        Set(K, "Num", RegValueType.DWord, 4294967295u),
        Set(K, "NumText", RegValueType.String, "42"),
        Set(K, "Long", RegValueType.QWord, ulong.MaxValue),
        Set(K, "Txt", RegValueType.String, "hello"),
        Set(K, "Exp", RegValueType.ExpandString, "%TEMP%\\x"),
        Set(K, "Multi", RegValueType.MultiString, new[] { "l1", "l2" }),
        Set(K, "BoolDefault", RegValueType.DWord, 1u),
        Set(K, "BoolCustom", RegValueType.String, "no"),
        Set(K, "BL1", RegValueType.DWord, 1u),
        Set(K, "BL2", RegValueType.DWord, 1u),
        Set(K + @"\Sub", "Sub", RegValueType.DWord, 0u),
        Set(K, "Mode", RegValueType.DWord, 1u),
        Set(K, "ModeExtra", RegValueType.String, "two"),
        RegistryOperation.DeleteAllValues(Hklm, K + @"\Prefixed"),
        Set(K + @"\Prefixed", "Item2", RegValueType.String, "second"),
        Set(K + @"\Prefixed", "Item1", RegValueType.String, "first"),
        RegistryOperation.DeleteAllValues(Hklm, K + @"\Numbered"),
        Set(K + @"\Numbered", "1", RegValueType.String, "n1"),
        RegistryOperation.DeleteAllValues(Hklm, K + @"\Explicit"),
        Set(K + @"\Explicit", "Name A", RegValueType.String, "Value A"),
        Set(K + @"\Explicit", "Name B", RegValueType.String, "Value B"),
        RegistryOperation.DeleteAllValues(Hklm, K + @"\NameIsValue"),
        Set(K + @"\NameIsValue", "x.exe", RegValueType.String, "x.exe"),
        Set(K + @"\Additive", "A1", RegValueType.String, "add1")
    ];

    [Fact]
    public void EnabledPolicyWithEveryElementTypeRecoversValuesExactly() {
        var result = PolicyMatcher.Match(BigEnabledOps(), Catalog(BigPolicy()));

        var setting = Assert.Single(result.Computer);
        Assert.Equal("Test:Big", setting.PolicyId);
        Assert.Equal(PolicyState.Enabled, setting.State);
        Assert.Empty(result.UnmatchedComputer);
        Assert.Empty(result.User);

        var v = setting.Values;
        Assert.Equal(4294967295UL, v["Num"].Number);
        Assert.Equal(42UL, v["NumText"].Number);
        Assert.Equal(ulong.MaxValue, v["Long"].Number);
        Assert.Equal("hello", v["Txt"].Text);
        Assert.Equal("%TEMP%\\x", v["Exp"].Text);
        Assert.Equal(new[] { "l1", "l2" }, v["Multi"].Lines);
        Assert.True(v["BoolDefault"].Boolean);
        Assert.False(v["BoolCustom"].Boolean);
        Assert.True(v["BoolList"].Boolean);
        Assert.False(v["BoolOverrideKey"].Boolean);
        Assert.Equal(2, v["Mode"].EnumIndex);
        Assert.Equal(new[] { "first", "second" }, v["Prefixed"].Entries!.Select(e => e.Value));
        Assert.Equal(new[] { "n1" }, v["Numbered"].Entries!.Select(e => e.Value));
        Assert.Equal(new[] { ("Name A", "Value A"), ("Name B", "Value B") }, v["Explicit"].Entries!.Select(e => (e.Name, e.Value)));
        Assert.Equal(new[] { "x.exe" }, v["NameIsValue"].Entries!.Select(e => e.Value));
        Assert.Equal(new[] { "add1" }, v["Additive"].Entries!.Select(e => e.Value));
    }

    [Fact]
    public void EnumPicksItemByValueAndValueList() {
        var result = PolicyMatcher.Match([
            Set(K, "Big", RegValueType.DWord, 1u),
            Set(K, "Mode", RegValueType.String, "custom")
        ], Catalog(BigPolicy()));
        Assert.Equal(3, Assert.Single(result.Computer).Values["Mode"].EnumIndex);

        result = PolicyMatcher.Match([Set(K, "Big", RegValueType.DWord, 1u), Set(K, "Mode", RegValueType.DWord, 0u)], Catalog(BigPolicy()));
        Assert.Equal(0, Assert.Single(result.Computer).Values["Mode"].EnumIndex);
    }

    [Fact]
    public void DisabledPolicyConsumesElementDeletes() {
        var ops = new List<RegistryOperation> {
            Set(K, "Big", RegValueType.DWord, 0u),
            RegistryOperation.DeleteValue(Hklm, K, "Num"),
            RegistryOperation.DeleteValue(Hklm, K, "Txt"),
            RegistryOperation.DeleteAllValues(Hklm, K + @"\Prefixed"),
            RegistryOperation.DeleteAllValues(Hklm, K + @"\Explicit")
        };
        var result = PolicyMatcher.Match(ops, Catalog(BigPolicy()));
        var setting = Assert.Single(result.Computer);
        Assert.Equal(PolicyState.Disabled, setting.State);
        Assert.Empty(setting.Values);
        Assert.Empty(result.UnmatchedComputer);
    }

    [Fact]
    public void ListOnlyPolicy_DeleteAllIsDisabled_AndDeleteKeyCountsAsDeleteAll() {
        var list = new ListElement { Id = "L", Key = K + @"\List", ValuePrefix = "" };
        var catalog = Catalog(Policy("OnlyList", K, null, elements: list));

        var disabled = PolicyMatcher.Match([RegistryOperation.DeleteAllValues(Hklm, K + @"\List")], catalog);
        Assert.Equal(PolicyState.Disabled, Assert.Single(disabled.Computer).State);

        var enabled = PolicyMatcher.Match([RegistryOperation.DeleteKey(Hklm, K + @"\List"), Set(K + @"\List", "1", RegValueType.String, "a")], catalog);
        var setting = Assert.Single(enabled.Computer);
        Assert.Equal(PolicyState.Enabled, setting.State);
        Assert.Equal("a", Assert.Single(setting.Values["L"].Entries!).Value);
        Assert.Empty(enabled.UnmatchedComputer);
    }

    // ------------------------------------------------------------------ scopes, ties, leftovers

    [Fact]
    public void ScopesAndPolicyClassesAreRespected() {
        var catalog = Catalog(
            Policy("M", K, "M", PolicyClass.Machine),
            Policy("U", K, "U", PolicyClass.User),
            Policy("B", K, "B", PolicyClass.Both));
        var result = PolicyMatcher.Match([
            Set(K, "M", RegValueType.DWord, 1u),
            Set(K, "U", RegValueType.DWord, 1u),
            Set(K, "B", RegValueType.DWord, 1u),
            Set(K, "M", RegValueType.DWord, 1u, Hkcu),
            Set(K, "U", RegValueType.DWord, 1u, Hkcu),
            Set(K, "B", RegValueType.DWord, 1u, Hkcu)
        ], catalog);

        Assert.Equal(["Test:B", "Test:M"], result.Computer.Select(s => s.PolicyId).Order().ToArray());
        Assert.Equal(["Test:B", "Test:U"], result.User.Select(s => s.PolicyId).Order().ToArray());
        Assert.Equal("U", Assert.Single(result.UnmatchedComputer).ValueName);
        Assert.Equal("M", Assert.Single(result.UnmatchedUser).ValueName);
    }

    [Fact]
    public void UnrelatedOperationsStayUnmatchedInOriginalOrder() {
        var catalog = Catalog(Policy("P", K, "Flag"));
        var ops = new List<RegistryOperation> {
            Set(@"Software\Random", "First", RegValueType.String, "1"),
            Set(K, "Flag", RegValueType.DWord, 1u),
            Set(K, "NotMine", RegValueType.DWord, 3u),
            RegistryOperation.DeleteKey(Hklm, @"Software\Random\Gone"),
            new RegistryOperation { Hive = Hklm, Kind = RegistryOperationKind.CreateKey, Key = @"Software\Made" }
        };
        var result = PolicyMatcher.Match(ops, catalog);
        Assert.Single(result.Computer);
        Assert.Equal([ops[0], ops[2], ops[3], ops[4]], result.UnmatchedComputer);
    }

    [Fact]
    public void SamePolicyShapedTwiceIsCommittedOnceByIdOrder() {
        var catalog = Catalog(Policy("B", K, "Flag"), Policy("A", K, "Flag"));
        var result = PolicyMatcher.Match([Set(K, "Flag", RegValueType.DWord, 1u)], catalog);
        Assert.Equal("Test:A", Assert.Single(result.Computer).PolicyId);
        Assert.Empty(result.UnmatchedComputer);
    }

    [Fact]
    public void CandidateExplainingMoreOperationsWins() {
        var small = Policy("Small", K, "Flag");
        var big = Policy("Big", K, "Flag", elements: new DecimalElement { Id = "N", ValueName = "N" });
        var result = PolicyMatcher.Match([Set(K, "Flag", RegValueType.DWord, 1u), Set(K, "N", RegValueType.DWord, 8u)], Catalog(small, big));

        var setting = Assert.Single(result.Computer);
        Assert.Equal("Test:Big", setting.PolicyId);
        Assert.Equal(8UL, setting.Values["N"].Number);
        Assert.Empty(result.UnmatchedComputer);
    }

    [Fact]
    public void PoliciesWithoutRegistryEvidenceAreNotReported() {
        var catalog = Catalog(BigPolicy(), Policy("Other", @"Software\Else", "X"));
        var result = PolicyMatcher.Match([Set(@"Software\Unrelated", "Y", RegValueType.DWord, 1u)], catalog);
        Assert.Empty(result.Computer);
        Assert.Empty(result.User);
        Assert.Single(result.UnmatchedComputer);
    }

    [Fact]
    public void ExplicitListDoesNotStealValuesOfOtherPolicies() {
        var list = new ListElement { Id = "L", ExplicitValue = true, Key = K + @"\Shared" };
        var catalog = Catalog(
            Policy("WithList", K, null, enabledList: VList(K, (null, "Marker", Dec(1))), elements: list),
            Policy("Neighbour", K + @"\Shared", "Fixed"));
        var result = PolicyMatcher.Match([
            Set(K, "Marker", RegValueType.DWord, 1u),
            Set(K + @"\Shared", "Fixed", RegValueType.DWord, 1u),
            Set(K + @"\Shared", "Entry", RegValueType.String, "v")
        ], catalog);

        Assert.Equal(["Test:Neighbour", "Test:WithList"], result.Computer.Select(s => s.PolicyId).Order().ToArray());
        var withList = result.Computer.Single(s => s.PolicyId == "Test:WithList");
        Assert.Equal("Entry", Assert.Single(withList.Values["L"].Entries!).Name);
    }
}

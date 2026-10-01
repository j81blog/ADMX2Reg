using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core.Tests;

public class PolicyEvaluatorTests {
    private const string K = @"Software\Policies\Test";
    private const RegistryHive HKLM = RegistryHive.LocalMachine;

    private static PolicyDefinition Policy(string? valueName = "V", PolicyValue? enabled = null, PolicyValue? disabled = null,
        PolicyValueList? enabledList = null, PolicyValueList? disabledList = null, PolicyClass cls = PolicyClass.Machine,
        PolicyPresentation? presentation = null, params PolicyElement[] elements) {
        var p = new PolicyDefinition {
            Namespace = "T", Name = "P", DisplayName = "P", SourceFile = "t.admx", Key = K, ValueName = valueName, Class = cls,
            EnabledValue = enabled, DisabledValue = disabled, EnabledList = enabledList, DisabledList = disabledList,
            Presentation = presentation
        };
        p.Elements.AddRange(elements);
        return p;
    }

    private static PolicySetting Setting(PolicyState state, params (string Id, PolicyElementValue Value)[] values) {
        var s = new PolicySetting { PolicyId = "T:P", State = state };
        foreach (var (id, v) in values) {
            s.Values[id] = v;
        }
        return s;
    }

    private static IReadOnlyList<RegistryOperation> Eval(PolicyDefinition p, PolicySetting s, PolicyScope scope = PolicyScope.Computer) =>
        PolicyEvaluator.Evaluate(p, s, scope);

    private static PolicyValue Dec(ulong n) => new(PolicyValueKind.Decimal, n);

    [Fact]
    public void NotConfigured_NoOperations() {
        Assert.Empty(Eval(Policy(), Setting(PolicyState.NotConfigured)));
    }

    [Fact]
    public void Enabled_NoEnabledValue_WritesDwordOne() {
        var ops = Eval(Policy(), Setting(PolicyState.Enabled));
        Assert.Equal(RegistryOperation.Set(HKLM, K, "V", RegValueType.DWord, 1u), Assert.Single(ops));
    }

    [Fact]
    public void Enabled_NoValueNameNoElements_WritesNothing() {
        Assert.Empty(Eval(Policy(valueName: null), Setting(PolicyState.Enabled)));
    }

    [Fact]
    public void Enabled_EnabledValueKinds() {
        Assert.Equal(RegistryOperation.Set(HKLM, K, "V", RegValueType.DWord, 7u), Eval(Policy(enabled: Dec(7)), Setting(PolicyState.Enabled)).Single());
        Assert.Equal(RegistryOperation.Set(HKLM, K, "V", RegValueType.QWord, 9ul),
            Eval(Policy(enabled: new PolicyValue(PolicyValueKind.LongDecimal, 9)), Setting(PolicyState.Enabled)).Single());
        Assert.Equal(RegistryOperation.Set(HKLM, K, "V", RegValueType.String, "x"),
            Eval(Policy(enabled: new PolicyValue(PolicyValueKind.String, 0, "x")), Setting(PolicyState.Enabled)).Single());
        Assert.Equal(RegistryOperation.DeleteValue(HKLM, K, "V"),
            Eval(Policy(enabled: new PolicyValue(PolicyValueKind.Delete)), Setting(PolicyState.Enabled)).Single());
    }

    [Fact]
    public void Scope_SelectsHive() {
        var ops = Eval(Policy(cls: PolicyClass.Both), Setting(PolicyState.Enabled), PolicyScope.User);
        Assert.Equal(RegistryHive.CurrentUser, ops.Single().Hive);
    }

    [Fact]
    public void Enabled_EnabledListUsesDefaultKeyAndItemKey() {
        var list = new PolicyValueList { DefaultKey = @"Software\Def" };
        list.Items.Add(new PolicyListItem(null, "a", Dec(1)));
        list.Items.Add(new PolicyListItem(@"Software\Own", "b", new PolicyValue(PolicyValueKind.String, 0, "s")));
        var plain = new PolicyValueList();
        plain.Items.Add(new PolicyListItem(null, "c", Dec(2)));

        var ops = Eval(Policy(valueName: null, enabledList: list), Setting(PolicyState.Enabled));
        Assert.Equal(RegistryOperation.Set(HKLM, @"Software\Def", "a", RegValueType.DWord, 1u), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, @"Software\Own", "b", RegValueType.String, "s"), ops[1]);

        var ops2 = Eval(Policy(valueName: null, enabledList: plain), Setting(PolicyState.Enabled));
        Assert.Equal(K, ops2.Single().Key);
    }

    [Fact]
    public void Boolean_DefaultsAndExplicitValuesAndLists() {
        var plain = new BooleanElement { Id = "B", ValueName = "B" };
        var p = Policy(valueName: null, elements: plain);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "B", RegValueType.DWord, 1u), Eval(p, Setting(PolicyState.Enabled, ("B", new() { Boolean = true }))).Single());
        Assert.Equal(RegistryOperation.Set(HKLM, K, "B", RegValueType.DWord, 0u), Eval(p, Setting(PolicyState.Enabled, ("B", new() { Boolean = false }))).Single());

        var tl = new PolicyValueList();
        tl.Items.Add(new PolicyListItem(null, "extra", Dec(3)));
        var rich = new BooleanElement {
            Id = "R", ValueName = "R", Key = @"Software\Other",
            TrueValue = new PolicyValue(PolicyValueKind.String, 0, "yes"),
            FalseValue = new PolicyValue(PolicyValueKind.Delete),
            TrueList = tl
        };
        var p2 = Policy(valueName: null, elements: rich);
        var on = Eval(p2, Setting(PolicyState.Enabled, ("R", new() { Boolean = true })));
        Assert.Equal(RegistryOperation.Set(HKLM, @"Software\Other", "R", RegValueType.String, "yes"), on[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, @"Software\Other", "extra", RegValueType.DWord, 3u), on[1]);
        var off = Eval(p2, Setting(PolicyState.Enabled, ("R", new() { Boolean = false })));
        Assert.Equal(RegistryOperation.DeleteValue(HKLM, @"Software\Other", "R"), off.Single());
    }

    [Fact]
    public void Boolean_AbsentValueUsesCheckBoxDefault() {
        var pres = new PolicyPresentation { Id = "x" };
        pres.Controls.Add(new CheckBoxControl { RefId = "B", DefaultChecked = true });
        var p = Policy(valueName: null, presentation: pres, elements: new BooleanElement { Id = "B", ValueName = "B" });
        Assert.Equal(1u, Eval(p, Setting(PolicyState.Enabled)).Single().Data);
    }

    [Fact]
    public void Decimal_DwordAndStoreAsText_AndPresentationDefault() {
        var d = new DecimalElement { Id = "D", ValueName = "D" };
        var t = new DecimalElement { Id = "T", ValueName = "T", StoreAsText = true };
        var p = Policy(valueName: null, elements: [d, t]);
        var ops = Eval(p, Setting(PolicyState.Enabled, ("D", new() { Number = 42 }), ("T", new() { Number = 42 })));
        Assert.Equal(RegistryOperation.Set(HKLM, K, "D", RegValueType.DWord, 42u), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "T", RegValueType.String, "42"), ops[1]);

        var pres = new PolicyPresentation { Id = "x" };
        pres.Controls.Add(new DecimalTextBoxControl { RefId = "D", DefaultValue = 15 });
        var withDefault = Policy(valueName: null, presentation: pres, elements: d);
        Assert.Equal(15u, Eval(withDefault, Setting(PolicyState.Enabled)).Single().Data);
        Assert.Empty(Eval(Policy(valueName: null, elements: d), Setting(PolicyState.Enabled)));
    }

    [Fact]
    public void LongDecimal_QwordAndText() {
        var q = new LongDecimalElement { Id = "Q", ValueName = "Q" };
        var t = new LongDecimalElement { Id = "T", ValueName = "T", StoreAsText = true };
        var big = 5_000_000_000ul;
        var ops = Eval(Policy(valueName: null, elements: [q, t]), Setting(PolicyState.Enabled, ("Q", new() { Number = big }), ("T", new() { Number = big })));
        Assert.Equal(RegistryOperation.Set(HKLM, K, "Q", RegValueType.QWord, big), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "T", RegValueType.String, "5000000000"), ops[1]);
    }

    [Fact]
    public void Text_StringAndExpandable() {
        var a = new TextElement { Id = "A", ValueName = "A" };
        var b = new TextElement { Id = "B", ValueName = "B", Expandable = true };
        var ops = Eval(Policy(valueName: null, elements: [a, b]), Setting(PolicyState.Enabled, ("A", new() { Text = "x" }), ("B", new() { Text = "%TEMP%" })));
        Assert.Equal(RegistryOperation.Set(HKLM, K, "A", RegValueType.String, "x"), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "B", RegValueType.ExpandString, "%TEMP%"), ops[1]);
    }

    [Fact]
    public void MultiText_MultiString() {
        var m = new MultiTextElement { Id = "M", ValueName = "M" };
        var op = Eval(Policy(valueName: null, elements: m), Setting(PolicyState.Enabled, ("M", new() { Lines = ["a", "b"] }))).Single();
        Assert.Equal(RegValueType.MultiString, op.Type);
        Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(op.Data));
    }

    [Fact]
    public void Enum_ItemValueAndValueList() {
        var vl = new PolicyValueList { DefaultKey = @"Software\E" };
        vl.Items.Add(new PolicyListItem(null, "extra", Dec(9)));
        var e = new EnumElement { Id = "E", ValueName = "E" };
        e.Items.Add(new EnumItem { DisplayName = "zero", Value = Dec(0) });
        e.Items.Add(new EnumItem { DisplayName = "text", Value = new PolicyValue(PolicyValueKind.String, 0, "two"), ValueList = vl });
        var p = Policy(valueName: null, elements: e);

        var ops = Eval(p, Setting(PolicyState.Enabled, ("E", new() { EnumIndex = 1 })));
        Assert.Equal(RegistryOperation.Set(HKLM, K, "E", RegValueType.String, "two"), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, @"Software\E", "extra", RegValueType.DWord, 9u), ops[1]);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "E", RegValueType.DWord, 0u), Eval(p, Setting(PolicyState.Enabled, ("E", new() { EnumIndex = 0 }))).Single());
        Assert.Empty(Eval(p, Setting(PolicyState.Enabled, ("E", new() { EnumIndex = 5 }))));
    }

    private static List<ListEntry> Entries(params (string Name, string Value)[] e) => e.Select(x => new ListEntry { Name = x.Name, Value = x.Value }).ToList();

    private static IReadOnlyList<RegistryOperation> EvalList(ListElement l, params (string Name, string Value)[] entries) =>
        Eval(Policy(valueName: null, elements: l), Setting(PolicyState.Enabled, ("L", new() { Entries = Entries(entries) })));

    [Fact]
    public void List_Explicit() {
        var ops = EvalList(new ListElement { Id = "L", Key = @"Software\L", ExplicitValue = true }, ("n1", "v1"), ("n2", "v2"));
        Assert.Equal(RegistryOperation.DeleteAllValues(HKLM, @"Software\L"), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, @"Software\L", "n1", RegValueType.String, "v1"), ops[1]);
        Assert.Equal(RegistryOperation.Set(HKLM, @"Software\L", "n2", RegValueType.String, "v2"), ops[2]);
    }

    [Fact]
    public void List_EmptyPrefixNumbersFromOne() {
        var ops = EvalList(new ListElement { Id = "L", ValuePrefix = "" }, ("", "a"), ("", "b"));
        Assert.Equal(RegistryOperation.DeleteAllValues(HKLM, K), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "1", RegValueType.String, "a"), ops[1]);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "2", RegValueType.String, "b"), ops[2]);
    }

    [Fact]
    public void List_PrefixX() {
        var ops = EvalList(new ListElement { Id = "L", ValuePrefix = "x" }, ("", "a"), ("", "b"));
        Assert.Equal("x1", ops[1].ValueName);
        Assert.Equal("x2", ops[2].ValueName);
    }

    [Fact]
    public void List_NoPrefixUsesValueAsName() {
        var ops = EvalList(new ListElement { Id = "L" }, ("", "a"));
        Assert.Equal(RegistryOperation.Set(HKLM, K, "a", RegValueType.String, "a"), ops[1]);
    }

    [Fact]
    public void List_AdditiveSkipsDeleteAll_ExpandableUsesExpandString() {
        var ops = EvalList(new ListElement { Id = "L", Additive = true, Expandable = true, ValuePrefix = "" }, ("", "%a%"));
        var op = Assert.Single(ops);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "1", RegValueType.ExpandString, "%a%"), op);
    }

    [Fact]
    public void List_EmptyEntriesStillClearsWhenNotAdditive() {
        var ops = EvalList(new ListElement { Id = "L" });
        Assert.Equal(RegistryOperation.DeleteAllValues(HKLM, K), Assert.Single(ops));
    }

    [Fact]
    public void Disabled_DefaultsDeleteValueAndElements() {
        var l = new ListElement { Id = "L", Key = @"Software\L" };
        var d = new DecimalElement { Id = "D", ValueName = "D" };
        var b = new BooleanElement { Id = "B", ValueName = "B", Key = @"Software\Other" };
        var noName = new BooleanElement { Id = "N" };
        var ops = Eval(Policy(elements: [l, d, b, noName]), Setting(PolicyState.Disabled));
        Assert.Equal(RegistryOperation.DeleteValue(HKLM, K, "V"), ops[0]);
        Assert.Equal(RegistryOperation.DeleteAllValues(HKLM, @"Software\L"), ops[1]);
        Assert.Equal(RegistryOperation.DeleteValue(HKLM, K, "D"), ops[2]);
        Assert.Equal(RegistryOperation.DeleteValue(HKLM, @"Software\Other", "B"), ops[3]);
        Assert.Equal(4, ops.Count);
    }

    [Fact]
    public void Disabled_DisabledValueAndList() {
        var list = new PolicyValueList();
        list.Items.Add(new PolicyListItem(null, "z", Dec(0)));
        var ops = Eval(Policy(disabled: Dec(0), disabledList: list), Setting(PolicyState.Disabled));
        Assert.Equal(RegistryOperation.Set(HKLM, K, "V", RegValueType.DWord, 0u), ops[0]);
        Assert.Equal(RegistryOperation.Set(HKLM, K, "z", RegValueType.DWord, 0u), ops[1]);
    }
}

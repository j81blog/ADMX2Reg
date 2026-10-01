using System.Diagnostics;
using ADMX2Reg.Core.Admx;

namespace ADMX2Reg.Core.Tests;

public class AdmxLoaderTests {
    private const string Real = @"C:\Windows\PolicyDefinitions";

    private static string WriteTempFolder(string admx, string adml, string language = "en-US") {
        var dir = Path.Combine(Path.GetTempPath(), "admx2reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, language));
        File.WriteAllText(Path.Combine(dir, "Test.admx"), admx);
        if (adml != null) {
            File.WriteAllText(Path.Combine(dir, language, "Test.adml"), adml);
        }
        return dir;
    }

    [Fact]
    public async Task RealFolder_LoadsPoliciesAndCategoryTree() {
        if (!Directory.Exists(Real)) {
            return;
        }
        var sw = Stopwatch.StartNew();
        var catalog = await AdmxLoader.LoadAsync(Real, "en-US");
        sw.Stop();
        Assert.True(catalog.Policies.Count > 1000, $"policies: {catalog.Policies.Count}");
        Assert.NotEmpty(catalog.RootCategories);
        Assert.Contains(catalog.RootCategories, c => c.Children.Count > 0);
        Assert.Equal("en-US", catalog.Language);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");

        var policy = catalog.Policies["Microsoft.Policies.WindowsUpdate:AutoUpdateCfg"];
        Assert.Equal("Configure Automatic Updates", policy.DisplayName);
        Assert.Equal(PolicyClass.Machine, policy.Class);
        Assert.Equal(@"Software\Policies\Microsoft\Windows\WindowsUpdate\AU", policy.Key);
        Assert.Equal("NoAutoUpdate", policy.ValueName);
        Assert.Equal(PolicyValueKind.Decimal, policy.EnabledValue!.Kind);
        Assert.Equal(0ul, policy.EnabledValue.Number);
        Assert.NotNull(policy.Category);
        Assert.Equal("Microsoft.Policies.WindowsUpdate:WindowsUpdateExperience", policy.Category!.Id);
        Assert.Contains(policy, policy.Category.Policies);
        Assert.False(string.IsNullOrEmpty(policy.SupportedOn));
        Assert.DoesNotContain("windows:", policy.SupportedOn);

        var en = Assert.IsType<EnumElement>(policy.Elements.Single(e => e.Id == "AutoUpdateMode"));
        Assert.True(en.Items.Count >= 4);
        var dropdown = policy.Presentation!.Controls.OfType<DropdownListControl>().First(c => c.RefId == "AutoUpdateMode");
        Assert.Equal(1, dropdown.DefaultItem);
        Assert.Equal("Configure automatic updating:", dropdown.Label);
        var check = policy.Presentation.Controls.OfType<CheckBoxControl>().First(c => c.RefId == "AutoUpdateSchEveryWeek");
        Assert.True(check.DefaultChecked);

        // Category chain reaches a root.
        var top = policy.Category;
        while (top.Parent != null) {
            top = top.Parent;
        }
        Assert.Contains(top, catalog.RootCategories);
        Console.WriteLine($"Loaded {catalog.Policies.Count} policies in {sw.ElapsedMilliseconds} ms, {catalog.Warnings.Count} warnings");
    }

    [Fact]
    public async Task RealFolder_ListWithEmptyValuePrefixIsKeptAsEmpty() {
        if (!Directory.Exists(Real)) {
            return;
        }
        var catalog = await AdmxLoader.LoadAsync(Real, "en-US");
        var list = catalog.Policies.Values.SelectMany(p => p.Elements).OfType<ListElement>().ToList();
        Assert.Contains(list, l => l.ValuePrefix == "");
        Assert.Contains(list, l => l.ValuePrefix == null);
    }

    [Fact]
    public async Task RealFolder_SecondLanguageLoads() {
        if (!Directory.Exists(Real) || !AdmxLoader.GetAvailableLanguages(Real).Contains("nl-NL")) {
            return;
        }
        var nl = await AdmxLoader.LoadAsync(Real, "nl-NL");
        var en = await AdmxLoader.LoadAsync(Real, "en-US");
        Assert.Equal("nl-NL", nl.Language);
        Assert.Equal(en.Policies.Count, nl.Policies.Count);
        Assert.NotEqual(en.Policies["Microsoft.Policies.WindowsUpdate:AutoUpdateCfg"].DisplayName,
            nl.Policies["Microsoft.Policies.WindowsUpdate:AutoUpdateCfg"].DisplayName);
    }

    [Fact]
    public void GetAvailableLanguages_ListsCultureFoldersWithAdml() {
        if (!Directory.Exists(Real)) {
            return;
        }
        var langs = AdmxLoader.GetAvailableLanguages(Real);
        Assert.Contains("en-US", langs);
    }

    [Fact]
    public void TryGetCentralStorePath_NeverThrows() {
        _ = AdmxLoader.TryGetCentralStorePath();
    }

    private const string Header = "<policyDefinitions xmlns=\"http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions\" revision=\"1.0\" schemaVersion=\"1.0\">";
    private const string HeaderRes = "<policyDefinitionResources xmlns=\"http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions\" revision=\"1.0\" schemaVersion=\"1.0\">";

    [Fact]
    public async Task HandBuilt_ResolvesRefsStringsAndElements() {
        var admx = Header + """
            <policyNamespaces><target prefix="t" namespace="Test.Ns"/><using prefix="o" namespace="Other.Ns"/></policyNamespaces>
            <supportedOn><definitions><definition name="SUP" displayName="$(string.Sup)"/></definitions></supportedOn>
            <categories>
              <category name="Child" displayName="$(string.Child)"><parentCategory ref="Root"/></category>
              <category name="Root" displayName="$(string.Root)"/>
              <category name="Orphan" displayName="$(string.Orphan)"><parentCategory ref="o:Missing"/></category>
            </categories>
            <policies>
              <policy name="P" class="Both" displayName="$(string.P)" explainText="$(string.P_Help)" presentation="$(presentation.P)" key="Software\K" valueName="V">
                <parentCategory ref="t:Child"/>
                <supportedOn ref="SUP"/>
                <enabledValue><string>on</string></enabledValue>
                <disabledValue><delete/></disabledValue>
                <elements>
                  <decimal id="D" valueName="D" minValue="2" storeAsText="true"/>
                  <list id="L" key="Software\L" valuePrefix="" additive="true"/>
                  <list id="L2" key="Software\L2"/>
                  <boolean id="B" valueName="B"><trueValue><decimal value="5"/></trueValue><falseList defaultKey="Software\F"><item valueName="x"><value><string>y</string></value></item></falseList></boolean>
                  <text id="T" valueName="T" expandable="true" maxLength="10"/>
                </elements>
              </policy>
              <policy name="P" class="User" displayName="dup" key="K"><parentCategory ref="Root"/></policy>
            </policies>
            </policyDefinitions>
            """;
        var adml = HeaderRes + """
            <resources><stringTable>
              <string id="Sup">Supported text</string><string id="Child">B child</string><string id="Root">Root</string><string id="Orphan">Orphan</string>
              <string id="P">My policy</string><string id="P_Help">Help text</string>
            </stringTable>
            <presentationTable><presentation id="P">
              <text>Hello</text>
              <decimalTextBox refId="D" defaultValue="7" spinStep="2">Number</decimalTextBox>
              <textBox refId="T"><label>Text label</label><defaultValue>abc</defaultValue></textBox>
              <comboBox refId="C"><label>Combo</label><default>d</default><suggestion>s1</suggestion><suggestion>s2</suggestion></comboBox>
              <multiTextBox refId="M" defaultHeight="5">Multi</multiTextBox>
              <listBox refId="L">List</listBox>
            </presentation></presentationTable></resources></policyDefinitionResources>
            """;
        var dir = WriteTempFolder(admx, adml);
        try {
            var catalog = await AdmxLoader.LoadAsync(dir, "en-US");
            var p = catalog.Policies["Test.Ns:P"];
            Assert.Equal("My policy", p.DisplayName);
            Assert.Equal("Help text", p.ExplainText);
            Assert.Equal("Supported text", p.SupportedOn);
            Assert.Equal("Test.Ns:Child", p.CategoryId);
            Assert.Equal("B child", p.Category!.DisplayName);
            Assert.Equal("Root", p.Category.Parent!.DisplayName);
            Assert.Equal(PolicyValueKind.String, p.EnabledValue!.Kind);
            Assert.Equal("on", p.EnabledValue.Text);
            Assert.Equal(PolicyValueKind.Delete, p.DisabledValue!.Kind);
            Assert.Contains(catalog.Warnings, w => w.Contains("duplicate policy"));

            var d = Assert.IsType<DecimalElement>(p.Elements[0]);
            Assert.Equal(2u, d.MinValue);
            Assert.Equal(9999u, d.MaxValue);
            Assert.True(d.StoreAsText);
            Assert.Equal("", Assert.IsType<ListElement>(p.Elements[1]).ValuePrefix);
            Assert.True(((ListElement)p.Elements[1]).Additive);
            Assert.Null(Assert.IsType<ListElement>(p.Elements[2]).ValuePrefix);
            var b = Assert.IsType<BooleanElement>(p.Elements[3]);
            Assert.Equal(5ul, b.TrueValue!.Number);
            Assert.Equal(@"Software\F", b.FalseList!.DefaultKey);
            Assert.Null(b.FalseList.Items[0].Key);
            var t = Assert.IsType<TextElement>(p.Elements[4]);
            Assert.True(t.Expandable);
            Assert.Equal(10, t.MaxLength);

            var c = p.Presentation!.Controls;
            Assert.Equal("Hello", Assert.IsType<TextLabelControl>(c[0]).Label);
            var dec = Assert.IsType<DecimalTextBoxControl>(c[1]);
            Assert.Equal(7ul, dec.DefaultValue);
            Assert.Equal(2ul, dec.SpinStep);
            Assert.Equal("Number", dec.Label);
            var tb = Assert.IsType<TextBoxControl>(c[2]);
            Assert.Equal("Text label", tb.Label);
            Assert.Equal("abc", tb.DefaultValue);
            var combo = Assert.IsType<ComboBoxControl>(c[3]);
            Assert.Equal("d", combo.DefaultValue);
            Assert.Equal(["s1", "s2"], combo.Suggestions);
            Assert.Equal(5, Assert.IsType<MultiTextBoxControl>(c[4]).DefaultHeight);
            Assert.Equal("List", Assert.IsType<ListBoxControl>(c[5]).Label);

            // Orphan category (parent in an unknown namespace) becomes a root with a warning.
            Assert.Contains(catalog.RootCategories, r => r.Name == "Orphan");
            Assert.Contains(catalog.Warnings, w => w.Contains("Orphan"));
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AdmlNextToAdmx_IsUsedWhenNoLanguageFolderHasIt() {
        var admx = Header + """
            <policyNamespaces><target prefix="t" namespace="Test.Ns"/></policyNamespaces>
            <categories><category name="Root" displayName="$(string.Root)"/></categories>
            <policies><policy name="P" class="User" displayName="$(string.P)" key="K"><parentCategory ref="Root"/></policy></policies>
            </policyDefinitions>
            """;
        var adml = HeaderRes + """
            <displayName/><description/>
            <resources><stringTable><string id="Root">Flat root</string><string id="P">Flat policy</string></stringTable></resources>
            </policyDefinitionResources>
            """;
        var dir = WriteTempFolder(admx, null!);
        try {
            File.WriteAllText(Path.Combine(dir, "Test.adml"), adml);
            var catalog = await AdmxLoader.LoadAsync(dir, "nl-NL");
            Assert.Equal("Flat policy", catalog.Policies["Test.Ns:P"].DisplayName);
            Assert.DoesNotContain(catalog.Warnings, w => w.Contains("ADML"));
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MissingAdml_LoadsWithRawNamesAndWarning() {
        var admx = Header + """
            <policyNamespaces><target prefix="t" namespace="Test.Ns"/></policyNamespaces>
            <categories><category name="Root" displayName="$(string.Root)"/></categories>
            <policies><policy name="P" class="User" displayName="$(string.P)" key="K"><parentCategory ref="Root"/></policy></policies>
            </policyDefinitions>
            """;
        var dir = WriteTempFolder(admx, null!);
        try {
            var catalog = await AdmxLoader.LoadAsync(dir, "nl-NL");
            Assert.Equal("P", catalog.Policies["Test.Ns:P"].DisplayName);
            Assert.Contains(catalog.Warnings, w => w.Contains("ADML"));
        } finally {
            Directory.Delete(dir, true);
        }
    }
}

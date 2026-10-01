using ADMX2Reg.Core.Admx;

namespace ADMX2Reg.Core.Tests;

public class WindowsSupportTests {
    private const string Real = @"C:\Windows\PolicyDefinitions";

    [Fact]
    public async Task RealFolder_WindowsVersionsAndSupport() {
        if (!Directory.Exists(Real)) {
            return;
        }
        var catalog = await AdmxLoader.LoadAsync(Real, "en-US");

        Assert.Contains(catalog.WindowsVersions, v => v.Index == 16);
        Assert.Contains(catalog.WindowsVersions, v => v.Index == 14);
        Assert.Equal(catalog.WindowsVersions.OrderByDescending(v => v.Index).Select(v => v.Index), catalog.WindowsVersions.Select(v => v.Index));
        Assert.NotEqual("MicrosoftWindows_11_0", catalog.WindowsVersions.First(v => v.Index == 16).DisplayName);

        // "At least Windows 10" is declared as range 14..15 but must be open ended.
        var gameDvr = catalog.Policies.Values.First(p => p.SourceFile == "GameDVR.admx" && p.Name == "AllowGameDVR");
        Assert.True(gameDvr.SupportsWindows(16));
        Assert.True(gameDvr.SupportsWindows(14));
        Assert.False(gameDvr.SupportsWindows(13));

        var preVista = catalog.Policies.Values.First(p => p.SourceFile == "AddRemovePrograms.admx" && p.Name == "NoAddFromCDorFloppy");
        Assert.False(preVista.SupportsWindows(16));

        Assert.Contains(catalog.Policies.Values, p => p.SourceFile == "inetres.admx" && p.WindowsSupport == null && p.SupportsWindows(16) == null);

        foreach (var index in new[] { 16, 14 }) {
            var supported = catalog.Policies.Values.Count(p => p.SupportsWindows(index) == true);
            var unsupported = catalog.Policies.Values.Count(p => p.SupportsWindows(index) == false);
            var none = catalog.Policies.Values.Count(p => p.SupportsWindows(index) == null);
            Assert.True(supported > 1800, $"index {index}: supported {supported}");
            Assert.InRange(unsupported, 200, 400);
            Assert.True(none > 500, $"index {index}: none {none}");
        }
    }

    private const string Admx = """
        <policyDefinitions xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <policyNamespaces><target prefix="t" namespace="Test.Policies" /></policyNamespaces>
          <supportedOn>
            <products>
              <product name="MicrosoftWindows" displayName="$(string.Win)">
                <majorVersion name="Win7" displayName="$(string.Win7)" versionIndex="6">
                  <minorVersion name="Win7_SP1" displayName="$(string.Win7SP1)" versionIndex="1" />
                </majorVersion>
                <majorVersion name="Win8" displayName="$(string.Win8)" versionIndex="8" />
                <majorVersion name="Win10" displayName="$(string.Win10)" versionIndex="14" />
                <majorVersion name="Win11" displayName="$(string.Win11)" versionIndex="16" />
              </product>
              <product name="Other" displayName="$(string.Other)" />
            </products>
            <definitions>
              <definition name="Range" displayName="$(string.Range)">
                <or><range ref="t:MicrosoftWindows" minVersionIndex="8" maxVersionIndex="14" /></or>
              </definition>
              <definition name="OpenMin" displayName="$(string.Range)"><or><range ref="t:MicrosoftWindows" maxVersionIndex="6" /></or></definition>
              <definition name="AtLeast" displayName="$(string.AtLeast)">
                <or><range ref="t:MicrosoftWindows" minVersionIndex="14" maxVersionIndex="15" /></or>
              </definition>
              <definition name="AtLeastRef" displayName="$(string.AtLeast)"><and><reference ref="t:Win8" /></and></definition>
              <definition name="RefMinor" displayName="$(string.Range)"><or><reference ref="t:Win7_SP1" /></or></definition>
              <definition name="RefProduct" displayName="$(string.Range)"><or><reference ref="t:MicrosoftWindows" /></or></definition>
              <definition name="Nested" displayName="$(string.Range)"><or><reference ref="t:Range" /><reference ref="t:Win11" /></or></definition>
              <definition name="Cycle" displayName="$(string.Range)"><or><reference ref="t:Cycle2" /></or></definition>
              <definition name="Cycle2" displayName="$(string.Range)"><or><reference ref="t:Cycle" /><reference ref="t:Win10" /></or></definition>
              <definition name="OtherOnly" displayName="$(string.Range)"><or><reference ref="t:Other" /></or></definition>
            </definitions>
          </supportedOn>
          <categories><category name="Cat" displayName="$(string.Cat)" /></categories>
          <policies>
            <policy name="P_Range" class="Machine" displayName="$(string.P)" key="Software\T" valueName="a"><parentCategory ref="Cat" /><supportedOn ref="Range" /></policy>
            <policy name="P_OpenMin" class="Machine" displayName="$(string.P)" key="Software\T" valueName="b"><parentCategory ref="Cat" /><supportedOn ref="OpenMin" /></policy>
            <policy name="P_AtLeast" class="Machine" displayName="$(string.P)" key="Software\T" valueName="c"><parentCategory ref="Cat" /><supportedOn ref="AtLeast" /></policy>
            <policy name="P_AtLeastRef" class="Machine" displayName="$(string.P)" key="Software\T" valueName="d"><parentCategory ref="Cat" /><supportedOn ref="AtLeastRef" /></policy>
            <policy name="P_RefMinor" class="Machine" displayName="$(string.P)" key="Software\T" valueName="e"><parentCategory ref="Cat" /><supportedOn ref="RefMinor" /></policy>
            <policy name="P_RefProduct" class="Machine" displayName="$(string.P)" key="Software\T" valueName="f"><parentCategory ref="Cat" /><supportedOn ref="RefProduct" /></policy>
            <policy name="P_Nested" class="Machine" displayName="$(string.P)" key="Software\T" valueName="g"><parentCategory ref="Cat" /><supportedOn ref="Nested" /></policy>
            <policy name="P_Cycle" class="Machine" displayName="$(string.P)" key="Software\T" valueName="h"><parentCategory ref="Cat" /><supportedOn ref="Cycle" /></policy>
            <policy name="P_OtherOnly" class="Machine" displayName="$(string.P)" key="Software\T" valueName="i"><parentCategory ref="Cat" /><supportedOn ref="OtherOnly" /></policy>
            <policy name="P_Missing" class="Machine" displayName="$(string.P)" key="Software\T" valueName="j"><parentCategory ref="Cat" /><supportedOn ref="Nope" /></policy>
            <policy name="P_None" class="Machine" displayName="$(string.P)" key="Software\T" valueName="k"><parentCategory ref="Cat" /></policy>
          </policies>
        </policyDefinitions>
        """;

    private const string Adml = """
        <policyDefinitionResources xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <resources><stringTable>
            <string id="Win">Windows</string><string id="Win7">Windows 7</string><string id="Win7SP1">Windows 7 SP1</string>
            <string id="Win8">Windows 8</string><string id="Win10">Windows 10</string><string id="Win11">Windows 11</string>
            <string id="Other">Other</string><string id="Range">Windows 8 to Windows 10</string>
            <string id="AtLeast">At least Windows 10</string><string id="Cat">Cat</string><string id="P">Policy</string>
          </stringTable></resources>
        </policyDefinitionResources>
        """;

    private static async Task<AdmxCatalog> LoadHandBuilt() {
        var dir = Path.Combine(Path.GetTempPath(), "admx2reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "en-US"));
        try {
            File.WriteAllText(Path.Combine(dir, "Test.admx"), Admx);
            File.WriteAllText(Path.Combine(dir, "en-US", "Test.adml"), Adml);
            return await AdmxLoader.LoadAsync(dir, "en-US");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task HandBuilt_RangesAtLeastRuleAndReferences() {
        var c = await LoadHandBuilt();
        PolicyDefinition P(string name) => c.Policies["Test.Policies:" + name];

        Assert.Equal([16, 14, 8, 6], c.WindowsVersions.Select(v => v.Index));
        Assert.Equal("Windows 11", c.WindowsVersions[0].DisplayName);

        // Explicit range keeps its upper bound.
        Assert.Equal([new VersionRange(8, 14)], P("P_Range").WindowsSupport);
        Assert.True(P("P_Range").SupportsWindows(14));
        Assert.False(P("P_Range").SupportsWindows(16));
        Assert.False(P("P_Range").SupportsWindows(7));

        // Missing minimum means 0.
        Assert.Equal([new VersionRange(0, 6)], P("P_OpenMin").WindowsSupport);

        // "At least" ignores the upper bound, also for a reference to a major version.
        Assert.Equal([new VersionRange(14, int.MaxValue)], P("P_AtLeast").WindowsSupport);
        Assert.True(P("P_AtLeast").SupportsWindows(16));
        Assert.Equal([new VersionRange(8, int.MaxValue)], P("P_AtLeastRef").WindowsSupport);

        // Minor version resolves to its major version; product reference means everything.
        Assert.Equal([new VersionRange(6, 6)], P("P_RefMinor").WindowsSupport);
        Assert.True(P("P_RefProduct").SupportsWindows(1));

        // Definition references recurse, cycles terminate.
        Assert.Contains(new VersionRange(8, 14), P("P_Nested").WindowsSupport!);
        Assert.Contains(new VersionRange(16, 16), P("P_Nested").WindowsSupport!);
        Assert.Equal([new VersionRange(14, 14)], P("P_Cycle").WindowsSupport);

        // Other products, unresolvable and missing references say nothing about Windows.
        Assert.Null(P("P_OtherOnly").WindowsSupport);
        Assert.Null(P("P_Missing").WindowsSupport);
        Assert.Null(P("P_None").WindowsSupport);
        Assert.Null(P("P_None").SupportsWindows(16));
    }
}

using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core.Tests;

public class HtmlReportTests {
    private static AdmxCatalog BuildCatalog() {
        var catalog = new AdmxCatalog { SourcePath = "", Language = "en-US" };
        var components = new PolicyCategory { Id = "T:Components", Name = "Components", DisplayName = "Windows Components" };
        var edge = new PolicyCategory { Id = "T:Edge", Name = "Edge", DisplayName = "Microsoft Edge", ParentId = components.Id, Parent = components };
        components.Children.Add(edge);
        catalog.Categories[components.Id] = components;
        catalog.Categories[edge.Id] = edge;
        catalog.RootCategories.Add(components);

        var policy = new PolicyDefinition {
            Namespace = "T",
            Name = "Home",
            DisplayName = "Configure <home> page & more",
            SourceFile = "t.admx",
            Class = PolicyClass.Both,
            Key = @"Software\Policies\Edge",
            ValueName = "Home",
            CategoryId = edge.Id,
            Category = edge
        };
        var mode = new EnumElement { Id = "Mode", ValueName = "Mode" };
        mode.Items.Add(new EnumItem { DisplayName = "Fast", Value = new PolicyValue(PolicyValueKind.Decimal, 0) });
        mode.Items.Add(new EnumItem { DisplayName = "Safe <b>", Value = new PolicyValue(PolicyValueKind.Decimal, 1) });
        policy.Elements.Add(new TextElement { Id = "Url", ValueName = "Url" });
        policy.Elements.Add(new DecimalElement { Id = "Count", ValueName = "Count" });
        policy.Elements.Add(new BooleanElement { Id = "Flag", ValueName = "Flag" });
        policy.Elements.Add(mode);
        policy.Elements.Add(new ListElement { Id = "Sites", Key = @"Software\Policies\Edge\Sites", ValuePrefix = "" });
        policy.Elements.Add(new ListElement { Id = "Pairs", Key = @"Software\Policies\Edge\Pairs", ExplicitValue = true });
        var presentation = new PolicyPresentation { Id = "pres" };
        presentation.Controls.Add(new TextBoxControl { RefId = "Url", Label = "Home page URL" });
        presentation.Controls.Add(new CheckBoxControl { RefId = "Flag", Label = "Use flag" });
        var withPresentation = new PolicyDefinition {
            Namespace = policy.Namespace, Name = policy.Name, DisplayName = policy.DisplayName, SourceFile = policy.SourceFile,
            Class = policy.Class, Key = policy.Key, ValueName = policy.ValueName, CategoryId = policy.CategoryId, Category = edge,
            Presentation = presentation
        };
        withPresentation.Elements.AddRange(policy.Elements);
        catalog.Policies[withPresentation.Id] = withPresentation;

        var plain = new PolicyDefinition {
            Namespace = "T", Name = "Plain", DisplayName = "Plain policy", SourceFile = "t.admx", Class = PolicyClass.Machine,
            Key = "Software\\Plain", ValueName = "P"
        };
        catalog.Policies[plain.Id] = plain;
        return catalog;
    }

    private static GpoDocument BuildGpo() {
        var gpo = new GpoDocument { Name = "Baseline <1>", Description = "Desc & \"quotes\"" };
        gpo.Computer.Add(new PolicySetting {
            PolicyId = "T:Home",
            State = PolicyState.Enabled,
            Comment = "because <reasons>",
            Values = new Dictionary<string, PolicyElementValue>(StringComparer.OrdinalIgnoreCase) {
                ["Url"] = new() { Text = "https://example.com/?a=1&b=2" },
                ["Count"] = new() { Number = 7 },
                ["Flag"] = new() { Boolean = true },
                ["Mode"] = new() { EnumIndex = 1 },
                ["Sites"] = new() { Entries = [new ListEntry { Value = "one.example" }, new ListEntry { Value = "two.example" }] },
                ["Pairs"] = new() { Entries = [new ListEntry { Name = "k", Value = "v" }] }
            }
        });
        gpo.Computer.Add(new PolicySetting { PolicyId = "T:Plain", State = PolicyState.Disabled });
        gpo.Computer.Add(new PolicySetting { PolicyId = "T:Gone", State = PolicyState.Enabled });
        gpo.Computer.Add(new PolicySetting { PolicyId = "T:Skipped", State = PolicyState.NotConfigured });
        gpo.ComputerExtraRegistry.Add(RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\Extra", "Num", RegValueType.DWord, 255u));
        gpo.ComputerExtraRegistry.Add(RegistryOperation.Set(RegistryHive.LocalMachine, @"Software\Extra", "Text", RegValueType.String, "<script>alert(1)</script>"));
        gpo.ComputerExtraRegistry.Add(RegistryOperation.DeleteKey(RegistryHive.LocalMachine, @"Software\Dead"));
        return gpo;
    }

    [Fact]
    public void Report_IsSelfContainedAndEncodesEverything() {
        var html = HtmlReportWriter.Build(BuildGpo(), BuildCatalog());

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Contains("Segoe UI", html);
        Assert.DoesNotContain("http://", html.Replace("https://example.com", ""));
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<link", html);

        Assert.Contains("Baseline &lt;1&gt;", html);
        Assert.Contains("Desc &amp; &quot;quotes&quot;", html);
        Assert.Contains("Configure &lt;home&gt; page &amp; more", html);
        Assert.Contains("because &lt;reasons&gt;", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public void Report_GroupsByCategoryPathAndRendersElementValues() {
        var html = HtmlReportWriter.Build(BuildGpo(), BuildCatalog());

        Assert.Contains("Computer Configuration", html);
        Assert.Contains("User Configuration", html);
        Assert.Contains("<details open>\n<summary>Windows Components/Microsoft Edge</summary>", html);
        Assert.Contains("Policies &gt; Administrative Templates", html);

        Assert.Contains("Home page URL:", html);
        Assert.Contains("https://example.com/?a=1&amp;b=2", html);
        Assert.Contains("Count:", html);   // no presentation label: falls back to element id
        Assert.Contains("Use flag:</span> Enabled", html);
        Assert.Contains("Safe &lt;b&gt;", html);
        Assert.Contains("one.example", html);
        Assert.Contains("<th>Value name</th>", html);
        Assert.Contains("<span class=\"chip off\">Disabled</span>", html);
    }

    [Fact]
    public void Report_ListsMissingPoliciesExtraRegistryAndEmptyScope() {
        var html = HtmlReportWriter.Build(BuildGpo(), BuildCatalog());

        Assert.Contains("Not found in loaded ADMX files", html);
        Assert.Contains("T:Gone", html);
        Assert.DoesNotContain("T:Skipped", html);

        Assert.Contains("Extra Registry Settings", html);
        Assert.Contains("HKEY_LOCAL_MACHINE\\Software\\Extra", html);
        Assert.Contains("REG_DWORD", html);
        Assert.Contains("0x000000ff (255)", html);
        Assert.Contains("Delete key and all subkeys", html);

        // User scope has nothing.
        var userIndex = html.IndexOf("<h2>User Configuration</h2>", StringComparison.Ordinal);
        Assert.Contains("No settings defined.", html[userIndex..]);
    }

    [Fact]
    public void Report_EmptyGpoShowsNoSettingsForBothScopes() {
        var html = HtmlReportWriter.Build(new GpoDocument { Name = "Empty" }, BuildCatalog());
        Assert.Equal(2, html.Split("No settings defined.").Length - 1);
    }
}

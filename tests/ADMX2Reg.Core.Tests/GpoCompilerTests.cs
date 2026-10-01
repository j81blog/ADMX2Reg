using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core.Tests;

public class GpoCompilerTests {
    private static (AdmxCatalog Catalog, PolicyDefinition A, PolicyDefinition B, PolicyDefinition C, PolicyDefinition UserOnly) Build() {
        var catalog = new AdmxCatalog { SourcePath = "x", Language = "en-US" };
        var root = new PolicyCategory { Id = "T:Root", Name = "Root", DisplayName = "Zeta" };
        var child = new PolicyCategory { Id = "T:Child", Name = "Child", DisplayName = "Alpha", Parent = root };
        PolicyDefinition Make(string name, string display, PolicyCategory cat, PolicyClass cls = PolicyClass.Machine) =>
            new() { Namespace = "T", Name = name, DisplayName = display, SourceFile = "t.admx", Key = @"Software\" + name, ValueName = name, Category = cat, Class = cls };
        var a = Make("A", "Banana", root);
        var b = Make("B", "Apple", root);
        var c = Make("C", "Cherry", child);
        var user = Make("U", "User one", root, PolicyClass.User);
        foreach (var cat in new[] { root, child }) {
            catalog.Categories[cat.Id] = cat;
        }
        foreach (var p in new[] { a, b, c, user }) {
            catalog.Policies[p.Id] = p;
        }
        return (catalog, a, b, c, user);
    }

    [Fact]
    public void Compile_OrdersByCategoryPathThenName_AppendsExtraAndForcesHive() {
        var (catalog, a, b, c, _) = Build();
        var gpo = new GpoDocument();
        gpo.Computer.Add(new PolicySetting { PolicyId = a.Id, State = PolicyState.Enabled });
        gpo.Computer.Add(new PolicySetting { PolicyId = c.Id, State = PolicyState.Enabled });
        gpo.Computer.Add(new PolicySetting { PolicyId = b.Id, State = PolicyState.Disabled });
        gpo.Computer.Add(new PolicySetting { PolicyId = "T:Nope", State = PolicyState.Enabled });
        gpo.Computer.Add(new PolicySetting { PolicyId = "T:Skipped", State = PolicyState.NotConfigured });
        // Wrong hive on purpose: must be forced to the scope hive.
        gpo.ComputerExtraRegistry.Add(RegistryOperation.Set(RegistryHive.CurrentUser, @"Software\Extra", "e", RegValueType.DWord, 1u));

        var result = GpoCompiler.Compile(gpo, catalog, PolicyScope.Computer);

        // "Zeta" < "Zeta\Alpha", so Apple, Banana (Zeta), then Cherry (Zeta\Alpha), then extras.
        Assert.Equal(["B", "A", "C", "e"], result.Operations.Select(o => o.ValueName));
        Assert.All(result.Operations, o => Assert.Equal(RegistryHive.LocalMachine, o.Hive));
        Assert.Equal(RegistryOperationKind.DeleteValue, result.Operations[0].Kind);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Policy 'T:Nope' not found in loaded ADMX files; skipped", warning);
    }

    [Fact]
    public void Compile_PolicyNotApplicableToScope_WarnsAndSkips() {
        var (catalog, a, _, _, user) = Build();
        var gpo = new GpoDocument();
        gpo.User.Add(new PolicySetting { PolicyId = a.Id, State = PolicyState.Enabled });
        gpo.User.Add(new PolicySetting { PolicyId = user.Id, State = PolicyState.Enabled });

        var result = GpoCompiler.Compile(gpo, catalog, PolicyScope.User);

        var op = Assert.Single(result.Operations);
        Assert.Equal("U", op.ValueName);
        Assert.Equal(RegistryHive.CurrentUser, op.Hive);
        Assert.Contains("T:A", Assert.Single(result.Warnings));
    }
}

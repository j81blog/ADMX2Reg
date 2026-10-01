using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class GpoCompiler {
    public static partial CompileResult Compile(GpoDocument gpo, AdmxCatalog catalog, PolicyScope scope) {
        var warnings = new List<string>();
        var hive = scope == PolicyScope.Computer ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var configured = new List<(string Path, PolicyDefinition Policy, PolicySetting Setting)>();

        foreach (var setting in gpo.Settings(scope)) {
            if (setting.State == PolicyState.NotConfigured) {
                continue;
            }
            if (!catalog.Policies.TryGetValue(setting.PolicyId, out var policy)) {
                warnings.Add($"Policy '{setting.PolicyId}' not found in loaded ADMX files; skipped");
                continue;
            }
            if (!policy.AppliesTo(scope)) {
                warnings.Add($"Policy '{setting.PolicyId}' ({policy.DisplayName}) does not apply to {scope} configuration; skipped");
                continue;
            }
            configured.Add((CategoryPath(policy.Category), policy, setting));
        }

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var ordered = configured
            .OrderBy(c => c.Path, comparer)
            .ThenBy(c => c.Policy.DisplayName, comparer)
            .ToList();

        var operations = new List<RegistryOperation>();
        foreach (var (_, policy, setting) in ordered) {
            operations.AddRange(PolicyEvaluator.Evaluate(policy, setting, scope));
        }
        operations.AddRange(gpo.ExtraRegistry(scope));

        return new CompileResult(operations.Select(o => o with { Hive = hive }).ToList(), warnings);
    }

    private static string CategoryPath(PolicyCategory? category) {
        var parts = new List<string>();
        for (var c = category; c != null; c = c.Parent) {
            parts.Add(c.DisplayName);
        }
        parts.Reverse();
        return string.Join("\\", parts);
    }
}

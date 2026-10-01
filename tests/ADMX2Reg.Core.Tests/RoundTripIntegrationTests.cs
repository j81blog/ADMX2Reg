using System.Text;
using ADMX2Reg.Core;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;
using Xunit.Abstractions;

namespace ADMX2Reg.Core.Tests;

/// <summary>
/// Every policy in the real local store: setting -> PolicyEvaluator -> .reg / .pol -> reader -> PolicyMatcher
/// -> PolicyEvaluator must produce the same registry operations, with nothing left unmatched.
/// </summary>
public class RoundTripIntegrationTests(ITestOutputHelper output) {
    private static readonly Lazy<AdmxCatalog?> Catalog = new(() =>
        Directory.Exists(AdmxLoader.DefaultPolicyDefinitionsPath)
            ? AdmxLoader.LoadAsync(AdmxLoader.DefaultPolicyDefinitionsPath, "en-US").GetAwaiter().GetResult()
            : null);

    [Theory]
    [InlineData("reg", PolicyState.Enabled)]
    [InlineData("reg", PolicyState.Disabled)]
    [InlineData("pol", PolicyState.Enabled)]
    [InlineData("pol", PolicyState.Disabled)]
    public void AllPoliciesRoundTrip(string format, PolicyState state) {
        var catalog = Catalog.Value;
        if (catalog is null) {
            return;
        }

        var failures = new List<string>();
        var total = 0;
        foreach (var policy in catalog.Policies.Values) {
            var scope = policy.AppliesTo(PolicyScope.Computer) ? PolicyScope.Computer : PolicyScope.User;
            var setting = BuildSetting(policy, state);
            var expected = PolicyEvaluator.Evaluate(policy, setting, scope);
            if (expected.Count == 0) {
                continue;
            }
            total++;

            var imported = format == "reg" ? ViaReg(expected) : ViaPol(expected, scope);
            var match = PolicyMatcher.Match(imported, catalog);
            var unmatched = scope == PolicyScope.Computer ? match.UnmatchedComputer : match.UnmatchedUser;
            var matched = scope == PolicyScope.Computer ? match.Computer : match.User;

            var actual = matched
                .SelectMany(s => PolicyEvaluator.Evaluate(catalog.Policies[s.PolicyId], s, scope))
                .Concat(unmatched)
                .ToList();

            var expectedSet = Normalize(expected);
            var actualSet = Normalize(actual);
            if (unmatched.Count > 0 || !expectedSet.SetEquals(actualSet)) {
                failures.Add($"{policy.Id}: unmatched={unmatched.Count}, matched=[{string.Join(", ", matched.Select(m => $"{m.PolicyId}={m.State}"))}]"
                    + $"\n    missing: {string.Join(" | ", expectedSet.Except(actualSet).Take(3))}"
                    + $"\n    extra:   {string.Join(" | ", actualSet.Except(expectedSet).Take(3))}");
            }
        }

        output.WriteLine($"{format}/{state}: {total} policies, {failures.Count} failures");
        foreach (var failure in failures.Take(40)) {
            output.WriteLine(failure);
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {total} policies did not round trip. First: {failures.FirstOrDefault()}");
    }

    private static IReadOnlyList<RegistryOperation> ViaReg(IReadOnlyList<RegistryOperation> ops) {
        var writer = new StringWriter();
        RegFileWriter.Write(ops, writer);
        return RegFileReader.Read(new StringReader(writer.ToString()), []);
    }

    private static IReadOnlyList<RegistryOperation> ViaPol(IReadOnlyList<RegistryOperation> ops, PolicyScope scope) {
        var stream = new MemoryStream();
        PolFileWriter.Write(ops, stream);
        stream.Position = 0;
        return PolFileReader.Read(stream, scope == PolicyScope.Computer ? RegistryHive.LocalMachine : RegistryHive.CurrentUser);
    }

    private static HashSet<string> Normalize(IEnumerable<RegistryOperation> ops) =>
        ops.Select(o => {
            var data = o.Data switch {
                null => "",
                string[] lines => string.Join("|", lines),
                byte[] bytes => Convert.ToHexString(bytes),
                _ => Convert.ToString(o.Data, System.Globalization.CultureInfo.InvariantCulture) ?? ""
            };
            return $"{o.Hive}\\{o.Key.ToLowerInvariant()}\\{o.ValueName?.ToLowerInvariant()}:{o.Kind}:{o.Type}:{data}";
        }).ToHashSet();

    /// <summary>Gives every element a non-default, non-empty value so all registry paths are exercised.</summary>
    private static PolicySetting BuildSetting(PolicyDefinition policy, PolicyState state) {
        var setting = new PolicySetting { PolicyId = policy.Id, State = state };
        if (state != PolicyState.Enabled) {
            return setting;
        }
        foreach (var element in policy.Elements) {
            PolicyElementValue? value = element switch {
                BooleanElement => new() { Boolean = true },
                DecimalElement d => new() { Number = Math.Max(d.MinValue, Math.Min(d.MaxValue, 7u)) },
                LongDecimalElement l => new() { Number = Math.Max(l.MinValue, Math.Min(l.MaxValue, 7ul)) },
                TextElement => new() { Text = "Text value" },
                MultiTextElement => new() { Lines = ["first", "second"] },
                EnumElement e when e.Items.Count > 0 => new() { EnumIndex = e.Items.Count - 1 },
                ListElement l => new() {
                    Entries = l.ExplicitValue
                        ? [new() { Name = "name1", Value = "value1" }, new() { Name = "name2", Value = "value2" }]
                        : [new() { Value = "value1" }, new() { Value = "value2" }]
                },
                _ => null
            };
            if (value is not null) {
                setting.Values[element.Id] = value;
            }
        }
        return setting;
    }
}

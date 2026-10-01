using System.Globalization;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

/// <remarks>
/// Reverse of the Group Policy engine. For every policy that touches a key used by the operations, the matcher
/// checks whether the operations explain an Enabled or a Disabled state (marker values plus element values).
/// Candidates are committed greedily, the one explaining the most operations first (ties: policy id), and
/// operations are never consumed twice. Everything left over is returned as unmatched, in the original order.
/// </remarks>
public static partial class PolicyMatcher {
    public static partial MatchResult Match(IEnumerable<RegistryOperation> operations, AdmxCatalog catalog) {
        var all = operations.ToList();
        var computer = MatchScope(all.Where(o => o.Hive == RegistryHive.LocalMachine).ToList(), catalog, PolicyScope.Computer);
        var user = MatchScope(all.Where(o => o.Hive == RegistryHive.CurrentUser).ToList(), catalog, PolicyScope.User);
        return new MatchResult(computer.Settings, user.Settings, computer.Unmatched, user.Unmatched);
    }

    private static (List<PolicySetting> Settings, List<RegistryOperation> Unmatched) MatchScope(
        List<RegistryOperation> ops, AdmxCatalog catalog, PolicyScope scope) {
        if (ops.Count == 0) {
            return ([], []);
        }

        var scopeOps = new ScopeOps(ops);
        var policies = catalog.Policies.Values.Where(p => p.AppliesTo(scope)).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var fixedNames = BuildFixedNames(policies);

        // Candidates: policies touching a key that appears in the operations.
        var candidates = new List<Candidate>();
        foreach (var policy in policies) {
            if (TouchedKeys(policy).Any(scopeOps.HasKey)) {
                var explanation = Explain(policy, scopeOps, fixedNames);
                if (explanation != null) {
                    candidates.Add(new Candidate(policy, explanation));
                }
            }
        }

        var committed = new List<Explanation>();
        while (candidates.Count > 0) {
            var best = candidates
                .OrderByDescending(c => c.Explanation!.Used.Count)
                .ThenBy(c => c.Policy.Id, StringComparer.Ordinal)
                .First();
            candidates.Remove(best);
            var chosen = best.Explanation!;
            committed.Add(chosen);
            foreach (var index in chosen.Used) {
                scopeOps.Consumed[index] = true;
            }

            for (var i = candidates.Count - 1; i >= 0; i--) {
                var other = candidates[i];
                if (!other.Explanation!.Used.Overlaps(chosen.Used)) {
                    continue;
                }
                var again = Explain(other.Policy, scopeOps, fixedNames);
                if (again == null) {
                    candidates.RemoveAt(i);
                } else {
                    candidates[i] = other with { Explanation = again };
                }
            }
        }

        var settings = committed
            .OrderBy(e => e.Used.Min())
            .ThenBy(e => e.Policy.Id, StringComparer.Ordinal)
            .Select(e => e.Setting)
            .ToList();
        var unmatched = ops.Where((_, i) => !scopeOps.Consumed[i]).ToList();
        return (settings, unmatched);
    }

    private sealed record Candidate(PolicyDefinition Policy, Explanation? Explanation);

    private sealed class Explanation(PolicyDefinition policy, PolicySetting setting, HashSet<int> used) {
        public PolicyDefinition Policy { get; } = policy;
        public PolicySetting Setting { get; } = setting;
        public HashSet<int> Used { get; } = used;
    }

    // ------------------------------------------------------------------ indexes

    private static string Norm(string? s) => (s ?? "").Trim('\\').ToLowerInvariant();

    private sealed class ScopeOps {
        public readonly List<RegistryOperation> Ops;
        public readonly bool[] Consumed;
        private readonly Dictionary<string, List<int>> _byKey = new();

        public ScopeOps(List<RegistryOperation> ops) {
            Ops = ops;
            Consumed = new bool[ops.Count];
            for (var i = 0; i < ops.Count; i++) {
                var key = Norm(ops[i].Key);
                if (!_byKey.TryGetValue(key, out var list)) {
                    _byKey[key] = list = [];
                }
                list.Add(i);
            }
        }

        public bool HasKey(string key) => _byKey.ContainsKey(Norm(key));

        public IReadOnlyList<int> OnKey(string key) => _byKey.TryGetValue(Norm(key), out var list) ? list : [];
    }

    /// <summary>(key, valueName) pairs that policies claim for fixed values, so list collection leaves them alone.</summary>
    private static Dictionary<(string, string), HashSet<string>> BuildFixedNames(List<PolicyDefinition> policies) {
        var map = new Dictionary<(string, string), HashSet<string>>();
        void Add(PolicyDefinition p, string? key, string? name) {
            if (name == null) {
                return;
            }
            var k = (Norm(key), name.ToLowerInvariant());
            if (!map.TryGetValue(k, out var set)) {
                map[k] = set = [];
            }
            set.Add(p.Id);
        }
        void AddList(PolicyDefinition p, PolicyValueList? list) {
            foreach (var item in list?.Items ?? []) {
                Add(p, item.Key ?? list!.DefaultKey ?? p.Key, item.ValueName);
            }
        }

        foreach (var p in policies) {
            Add(p, p.Key, p.ValueName);
            AddList(p, p.EnabledList);
            AddList(p, p.DisabledList);
            foreach (var e in p.Elements) {
                if (e is not ListElement) {
                    Add(p, e.Key ?? p.Key, e.ValueName);
                }
                if (e is BooleanElement b) {
                    AddList(p, b.TrueList);
                    AddList(p, b.FalseList);
                } else if (e is EnumElement en) {
                    foreach (var item in en.Items) {
                        AddList(p, item.ValueList);
                    }
                }
            }
        }
        return map;
    }

    private static IEnumerable<string> TouchedKeys(PolicyDefinition p) {
        yield return p.Key;
        foreach (var e in p.Elements) {
            yield return e.Key ?? p.Key;
            if (e is BooleanElement b) {
                foreach (var k in ListKeys(p, b.TrueList).Concat(ListKeys(p, b.FalseList))) {
                    yield return k;
                }
            } else if (e is EnumElement en) {
                foreach (var item in en.Items) {
                    foreach (var k in ListKeys(p, item.ValueList)) {
                        yield return k;
                    }
                }
            }
        }
        foreach (var k in ListKeys(p, p.EnabledList).Concat(ListKeys(p, p.DisabledList))) {
            yield return k;
        }
    }

    private static IEnumerable<string> ListKeys(PolicyDefinition p, PolicyValueList? list) =>
        (list?.Items ?? []).Select(i => i.Key ?? list!.DefaultKey ?? p.Key);

    // ------------------------------------------------------------------ explaining one policy

    /// <summary>One attempt to explain a policy against the still unconsumed operations.</summary>
    private sealed class Trial(ScopeOps scopeOps, Dictionary<(string, string), HashSet<string>> fixedNames, PolicyDefinition policy) {
        public readonly HashSet<int> Used = [];
        /// <summary>Operations that prove the state (marker, element values, deletes). DeleteAllValues does not count for Enabled.</summary>
        public int Evidence;

        public bool IsFree(int index) => !scopeOps.Consumed[index] && !Used.Contains(index);

        public int Find(string key, string? name, Func<RegistryOperation, bool> predicate) {
            foreach (var i in scopeOps.OnKey(key)) {
                var op = scopeOps.Ops[i];
                if (IsFree(i) && string.Equals(op.ValueName ?? "", name ?? "", StringComparison.OrdinalIgnoreCase) && predicate(op)) {
                    return i;
                }
            }
            return -1;
        }

        public int FindKeyOp(string key, params RegistryOperationKind[] kinds) {
            foreach (var i in scopeOps.OnKey(key)) {
                if (IsFree(i) && kinds.Contains(scopeOps.Ops[i].Kind)) {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Consumes the operation matching a policy value (Set with equal data, or DeleteValue for a delete).</summary>
        public bool TakeValue(string key, string? name, PolicyValue value, bool countAsEvidence = true) {
            var index = value.Kind == PolicyValueKind.Delete
                ? Find(key, name, o => o.Kind == RegistryOperationKind.DeleteValue)
                : Find(key, name, o => o.Kind == RegistryOperationKind.SetValue && Matches(value, o));
            if (index < 0) {
                return false;
            }
            Used.Add(index);
            if (countAsEvidence) {
                Evidence++;
            }
            return true;
        }

        /// <summary>Consumes every item of a value list; false (nothing kept) when any item is missing.</summary>
        public bool TakeList(PolicyValueList list) {
            var snapshotUsed = Used.ToList();
            var snapshotEvidence = Evidence;
            foreach (var item in list.Items) {
                if (!TakeValue(item.Key ?? list.DefaultKey ?? policy.Key, item.ValueName, item.Value)) {
                    Used.Clear();
                    Used.UnionWith(snapshotUsed);
                    Evidence = snapshotEvidence;
                    return false;
                }
            }
            return true;
        }

        public bool IsFixedNameOfOtherPolicy(string key, string name) =>
            fixedNames.TryGetValue((Norm(key), name.ToLowerInvariant()), out var owners) && owners.Any(id => id != policy.Id);

        public ScopeOps Ops => scopeOps;
    }

    private static bool Matches(PolicyValue value, RegistryOperation op) {
        switch (value.Kind) {
            case PolicyValueKind.Decimal:
                return op.Type == RegValueType.DWord && ToUInt64(op.Data) == value.Number;
            case PolicyValueKind.LongDecimal:
                return op.Type == RegValueType.QWord && ToUInt64(op.Data) == value.Number;
            case PolicyValueKind.String:
                return op.Type is RegValueType.String or RegValueType.ExpandString
                    && string.Equals(op.Data as string ?? "", value.Text ?? "", StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static ulong ToUInt64(object? data) => data switch {
        uint u => u,
        ulong ul => ul,
        _ => ulong.MaxValue
    };

    private static Explanation? Explain(PolicyDefinition policy, ScopeOps ops, Dictionary<(string, string), HashSet<string>> fixedNames) {
        var enabled = TryEnabled(policy, new Trial(ops, fixedNames, policy));
        var disabled = TryDisabled(policy, new Trial(ops, fixedNames, policy));
        if (enabled == null) {
            return disabled;
        }
        if (disabled == null) {
            return enabled;
        }
        return disabled.Used.Count > enabled.Used.Count ? disabled : enabled;
    }

    private static Explanation? TryEnabled(PolicyDefinition p, Trial t) {
        var hasMarker = false;

        if (p.ValueName != null) {
            hasMarker = true;
            if (!t.TakeValue(p.Key, p.ValueName, p.EnabledValue ?? new PolicyValue(PolicyValueKind.Decimal, 1))) {
                return null;
            }
        }
        if (p.EnabledList is { Items.Count: > 0 } enabledList) {
            hasMarker = true;
            if (!t.TakeList(enabledList)) {
                return null;
            }
        }
        var values = new Dictionary<string, PolicyElementValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in p.Elements) {
            var value = ReadElement(p, element, t);
            if (value != null) {
                values[element.Id] = value;
            }
        }

        if (!hasMarker && t.Evidence == 0) {
            return null;
        }
        if (t.Used.Count == 0) {
            return null;
        }
        return new Explanation(p, new PolicySetting { PolicyId = p.Id, State = PolicyState.Enabled, Values = values }, t.Used);
    }

    private static Explanation? TryDisabled(PolicyDefinition p, Trial t) {
        var hasMarker = false;

        if (p.ValueName != null) {
            hasMarker = true;
            var ok = p.DisabledValue != null
                ? t.TakeValue(p.Key, p.ValueName, p.DisabledValue)
                : t.TakeValue(p.Key, p.ValueName, new PolicyValue(PolicyValueKind.Delete));
            if (!ok) {
                return null;
            }
        }
        if (p.DisabledList is { Items.Count: > 0 } disabledList) {
            hasMarker = true;
            if (!t.TakeList(disabledList)) {
                return null;
            }
        }

        // Element cleanup the engine writes for a disabled policy; optional, counts as evidence only without a marker.
        foreach (var element in p.Elements) {
            var key = element.Key ?? p.Key;
            if (element is ListElement) {
                var index = t.FindKeyOp(key, RegistryOperationKind.DeleteAllValues);
                if (index < 0) {
                    index = t.FindKeyOp(key, RegistryOperationKind.DeleteKey);
                }
                if (index >= 0) {
                    t.Used.Add(index);
                    t.Evidence++;
                }
            } else if (element.ValueName != null) {
                var index = t.Find(key, element.ValueName, o => o.Kind == RegistryOperationKind.DeleteValue);
                if (index >= 0) {
                    t.Used.Add(index);
                    t.Evidence++;
                }
            }
        }

        if (!hasMarker && t.Evidence == 0) {
            return null;
        }
        if (t.Used.Count == 0) {
            return null;
        }
        return new Explanation(p, new PolicySetting { PolicyId = p.Id, State = PolicyState.Disabled }, t.Used);
    }

    // ------------------------------------------------------------------ element values

    private static PolicyElementValue? ReadElement(PolicyDefinition p, PolicyElement element, Trial t) {
        var key = element.Key ?? p.Key;
        switch (element) {
            case BooleanElement b:
                return ReadBoolean(p, b, key, t);
            case DecimalElement:
            case LongDecimalElement: {
                if (element.ValueName == null) {
                    return null;
                }
                var index = t.Find(key, element.ValueName, o => o.Kind == RegistryOperationKind.SetValue && ParseNumber(o) != null);
                if (index < 0) {
                    return null;
                }
                t.Used.Add(index);
                t.Evidence++;
                return new PolicyElementValue { Number = ParseNumber(t.Ops.Ops[index]) };
            }
            case TextElement: {
                if (element.ValueName == null) {
                    return null;
                }
                var index = t.Find(key, element.ValueName, o => o.Kind == RegistryOperationKind.SetValue && o.Type is RegValueType.String or RegValueType.ExpandString);
                if (index < 0) {
                    return null;
                }
                t.Used.Add(index);
                t.Evidence++;
                return new PolicyElementValue { Text = t.Ops.Ops[index].Data as string ?? "" };
            }
            case MultiTextElement: {
                if (element.ValueName == null) {
                    return null;
                }
                var index = t.Find(key, element.ValueName, o => o.Kind == RegistryOperationKind.SetValue && o.Type == RegValueType.MultiString);
                if (index < 0) {
                    return null;
                }
                t.Used.Add(index);
                t.Evidence++;
                return new PolicyElementValue { Lines = (t.Ops.Ops[index].Data as string[] ?? []).ToList() };
            }
            case EnumElement e:
                return ReadEnum(p, e, key, t);
            case ListElement l:
                return ReadList(p, l, key, t);
            default:
                return null;
        }
    }

    private static ulong? ParseNumber(RegistryOperation op) {
        switch (op.Type) {
            case RegValueType.DWord:
            case RegValueType.QWord:
                return ToUInt64(op.Data);
            case RegValueType.String:
            case RegValueType.ExpandString:
                return ulong.TryParse(op.Data as string, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
            default:
                return null;
        }
    }

    private static PolicyElementValue? ReadBoolean(PolicyDefinition p, BooleanElement b, string key, Trial t) {
        var trueValue = b.TrueValue ?? new PolicyValue(PolicyValueKind.Decimal, 1);
        var falseValue = b.FalseValue ?? new PolicyValue(PolicyValueKind.Decimal, 0);

        bool? Try(PolicyValue value, PolicyValueList? list) {
            var hasValue = b.ValueName != null;
            var hasList = list is { Items.Count: > 0 };
            if (!hasValue && !hasList) {
                return null;
            }
            var snapshot = t.Used.ToList();
            var evidence = t.Evidence;
            if (hasValue && !t.TakeValue(key, b.ValueName, value)) {
                return false;
            }
            if (hasList && !t.TakeList(list!)) {
                t.Used.Clear();
                t.Used.UnionWith(snapshot);
                t.Evidence = evidence;
                return false;
            }
            return true;
        }

        if (Try(trueValue, b.TrueList) == true) {
            return new PolicyElementValue { Boolean = true };
        }
        if (Try(falseValue, b.FalseList) == true) {
            return new PolicyElementValue { Boolean = false };
        }
        return null;
    }

    private static PolicyElementValue? ReadEnum(PolicyDefinition p, EnumElement e, string key, Trial t) {
        var bestIndex = -1;
        var bestCount = 0;
        HashSet<int>? bestUsed = null;
        var bestEvidence = 0;

        for (var i = 0; i < e.Items.Count; i++) {
            var item = e.Items[i];
            var snapshot = t.Used.ToList();
            var evidence = t.Evidence;

            var ok = true;
            if (e.ValueName != null) {
                ok = t.TakeValue(key, e.ValueName, item.Value);
            }
            if (ok && item.ValueList is { Items.Count: > 0 }) {
                ok = t.TakeList(item.ValueList);
            }
            var added = t.Used.Count - snapshot.Count;
            if (ok && added > bestCount) {
                bestCount = added;
                bestIndex = i;
                bestUsed = [.. t.Used];
                bestEvidence = t.Evidence;
            }

            t.Used.Clear();
            t.Used.UnionWith(snapshot);
            t.Evidence = evidence;
        }

        if (bestIndex < 0) {
            return null;
        }
        t.Used.UnionWith(bestUsed!);
        t.Evidence = bestEvidence;
        return new PolicyElementValue { EnumIndex = bestIndex };
    }

    private static PolicyElementValue? ReadList(PolicyDefinition p, ListElement l, string key, Trial t) {
        var consumedAny = false;
        if (!l.Additive) {
            var index = t.FindKeyOp(key, RegistryOperationKind.DeleteAllValues);
            if (index < 0) {
                index = t.FindKeyOp(key, RegistryOperationKind.DeleteKey);
            }
            if (index >= 0) {
                t.Used.Add(index);
                consumedAny = true;
            }
        }

        var found = new List<(int Index, long Order, ListEntry Entry)>();
        foreach (var i in t.Ops.OnKey(key)) {
            var op = t.Ops.Ops[i];
            if (!t.IsFree(i) || op.Kind != RegistryOperationKind.SetValue || op.Type is not (RegValueType.String or RegValueType.ExpandString)) {
                continue;
            }
            var name = op.ValueName ?? "";
            var value = op.Data as string ?? "";
            if (t.IsFixedNameOfOtherPolicy(key, name)) {
                continue;
            }

            if (l.ExplicitValue) {
                found.Add((i, i, new ListEntry { Name = name, Value = value }));
            } else if (l.ValuePrefix != null) {
                var prefix = l.ValuePrefix;
                var rest = name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : null;
                if (rest != null && rest.All(char.IsAsciiDigit) && long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) {
                    found.Add((i, n, new ListEntry { Value = value }));
                }
            } else if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) {
                found.Add((i, i, new ListEntry { Value = value }));
            }
        }

        foreach (var f in found) {
            t.Used.Add(f.Index);
            t.Evidence++;
        }
        if (found.Count == 0 && !consumedAny) {
            return null;
        }
        var entries = l.ValuePrefix != null && !l.ExplicitValue
            ? found.OrderBy(f => f.Order).ThenBy(f => f.Index).Select(f => f.Entry).ToList()
            : found.Select(f => f.Entry).ToList();
        return new PolicyElementValue { Entries = entries };
    }
}

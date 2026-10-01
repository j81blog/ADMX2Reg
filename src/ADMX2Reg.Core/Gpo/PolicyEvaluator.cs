using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class PolicyEvaluator {
    public static partial IReadOnlyList<RegistryOperation> Evaluate(PolicyDefinition definition, PolicySetting setting, PolicyScope scope) {
        var ops = new List<RegistryOperation>();
        var hive = scope == PolicyScope.Computer ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

        switch (setting.State) {
            case PolicyState.Enabled:
                Enabled(definition, setting, hive, ops);
                break;
            case PolicyState.Disabled:
                Disabled(definition, hive, ops);
                break;
        }
        return ops;
    }

    private static void Enabled(PolicyDefinition def, PolicySetting setting, RegistryHive hive, List<RegistryOperation> ops) {
        if (def.EnabledValue != null) {
            if (def.ValueName != null) {
                AddValue(ops, hive, def.Key, def.ValueName, def.EnabledValue);
            }
        } else if (def.ValueName != null) {
            ops.Add(RegistryOperation.Set(hive, def.Key, def.ValueName, RegValueType.DWord, 1u));
        }
        AddList(ops, hive, def.EnabledList, def.Key);

        foreach (var element in def.Elements) {
            var key = element.Key ?? def.Key;
            setting.Values.TryGetValue(element.Id, out var value);
            switch (element) {
                case BooleanElement b:
                    Boolean(def, b, key, value, hive, ops);
                    break;
                case DecimalElement d: {
                    var number = value?.Number ?? PresentationDefault<DecimalTextBoxControl>(def, d.Id)?.DefaultValue;
                    if (number != null && d.ValueName != null) {
                        if (d.StoreAsText) {
                            ops.Add(RegistryOperation.Set(hive, key, d.ValueName, RegValueType.String, number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        } else {
                            ops.Add(RegistryOperation.Set(hive, key, d.ValueName, RegValueType.DWord, unchecked((uint)number.Value)));
                        }
                    }
                    break;
                }
                case LongDecimalElement l: {
                    var number = value?.Number ?? PresentationDefault<LongDecimalTextBoxControl>(def, l.Id)?.DefaultValue;
                    if (number != null && l.ValueName != null) {
                        if (l.StoreAsText) {
                            ops.Add(RegistryOperation.Set(hive, key, l.ValueName, RegValueType.String, number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        } else {
                            ops.Add(RegistryOperation.Set(hive, key, l.ValueName, RegValueType.QWord, number.Value));
                        }
                    }
                    break;
                }
                case TextElement t: {
                    var text = value?.Text;
                    if (text == null && value == null) {
                        var fallback = PresentationDefault<TextBoxControl>(def, t.Id)?.DefaultValue
                            ?? PresentationDefault<ComboBoxControl>(def, t.Id)?.DefaultValue;
                        text = string.IsNullOrEmpty(fallback) ? null : fallback;
                    }
                    if (text != null && t.ValueName != null) {
                        ops.Add(RegistryOperation.Set(hive, key, t.ValueName, t.Expandable ? RegValueType.ExpandString : RegValueType.String, text));
                    }
                    break;
                }
                case MultiTextElement m: {
                    if (value?.Lines != null && m.ValueName != null) {
                        ops.Add(RegistryOperation.Set(hive, key, m.ValueName, RegValueType.MultiString, value.Lines.ToArray()));
                    }
                    break;
                }
                case EnumElement e:
                    Enum(def, e, key, value, hive, ops);
                    break;
                case ListElement l:
                    List(l, key, value, hive, ops);
                    break;
            }
        }
    }

    private static void Boolean(PolicyDefinition def, BooleanElement b, string key, PolicyElementValue? value, RegistryHive hive, List<RegistryOperation> ops) {
        var state = value?.Boolean ?? PresentationDefault<CheckBoxControl>(def, b.Id)?.DefaultChecked ?? false;
        var chosen = state ? b.TrueValue : b.FalseValue;
        if (b.ValueName != null) {
            if (chosen != null) {
                AddValue(ops, hive, key, b.ValueName, chosen);
            } else {
                ops.Add(RegistryOperation.Set(hive, key, b.ValueName, RegValueType.DWord, state ? 1u : 0u));
            }
        }
        AddList(ops, hive, state ? b.TrueList : b.FalseList, key);
    }

    private static void Enum(PolicyDefinition def, EnumElement e, string key, PolicyElementValue? value, RegistryHive hive, List<RegistryOperation> ops) {
        var index = value?.EnumIndex ?? PresentationDefault<DropdownListControl>(def, e.Id)?.DefaultItem;
        if (index == null || index < 0 || index >= e.Items.Count) {
            return;
        }
        var item = e.Items[index.Value];
        if (e.ValueName != null) {
            AddValue(ops, hive, key, e.ValueName, item.Value);
        }
        AddList(ops, hive, item.ValueList, key);
    }

    private static void List(ListElement l, string key, PolicyElementValue? value, RegistryHive hive, List<RegistryOperation> ops) {
        if (value?.Entries == null) {
            return;
        }
        if (!l.Additive) {
            ops.Add(RegistryOperation.DeleteAllValues(hive, key));
        }
        var type = l.Expandable ? RegValueType.ExpandString : RegValueType.String;
        for (var i = 0; i < value.Entries.Count; i++) {
            var entry = value.Entries[i];
            string name;
            string data;
            if (l.ExplicitValue) {
                if (string.IsNullOrEmpty(entry.Name)) {
                    continue;
                }
                name = entry.Name;
                data = entry.Value;
            } else {
                if (string.IsNullOrEmpty(entry.Value)) {
                    continue;
                }
                data = entry.Value;
                name = l.ValuePrefix != null ? l.ValuePrefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : entry.Value;
            }
            ops.Add(RegistryOperation.Set(hive, key, name, type, data));
        }
    }

    private static void Disabled(PolicyDefinition def, RegistryHive hive, List<RegistryOperation> ops) {
        if (def.DisabledValue != null) {
            if (def.ValueName != null) {
                AddValue(ops, hive, def.Key, def.ValueName, def.DisabledValue);
            }
        } else if (def.ValueName != null) {
            ops.Add(RegistryOperation.DeleteValue(hive, def.Key, def.ValueName));
        }
        AddList(ops, hive, def.DisabledList, def.Key);

        foreach (var element in def.Elements) {
            var key = element.Key ?? def.Key;
            if (element is ListElement) {
                ops.Add(RegistryOperation.DeleteAllValues(hive, key));
            } else if (element.ValueName != null) {
                ops.Add(RegistryOperation.DeleteValue(hive, key, element.ValueName));
            }
        }
    }

    private static void AddList(List<RegistryOperation> ops, RegistryHive hive, PolicyValueList? list, string policyKey) {
        if (list == null) {
            return;
        }
        foreach (var item in list.Items) {
            AddValue(ops, hive, item.Key ?? list.DefaultKey ?? policyKey, item.ValueName, item.Value);
        }
    }

    private static void AddValue(List<RegistryOperation> ops, RegistryHive hive, string key, string valueName, PolicyValue value) {
        switch (value.Kind) {
            case PolicyValueKind.Decimal:
                ops.Add(RegistryOperation.Set(hive, key, valueName, RegValueType.DWord, unchecked((uint)value.Number)));
                break;
            case PolicyValueKind.LongDecimal:
                ops.Add(RegistryOperation.Set(hive, key, valueName, RegValueType.QWord, value.Number));
                break;
            case PolicyValueKind.String:
                ops.Add(RegistryOperation.Set(hive, key, valueName, RegValueType.String, value.Text ?? ""));
                break;
            default:
                ops.Add(RegistryOperation.DeleteValue(hive, key, valueName));
                break;
        }
    }

    private static T? PresentationDefault<T>(PolicyDefinition def, string elementId) where T : PresentationControl =>
        def.Presentation?.Controls.OfType<T>().FirstOrDefault(c => string.Equals(c.RefId, elementId, StringComparison.OrdinalIgnoreCase));
}

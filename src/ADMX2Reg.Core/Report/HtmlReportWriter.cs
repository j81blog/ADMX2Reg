using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class HtmlReportWriter {
    private const string Css = """
        :root {
            --bg: #f3f5f8; --surface: #ffffff; --text: #1b1f24; --muted: #5b6573; --border: #d9dee6;
            --head: #0f4c81; --head-text: #ffffff; --row-alt: #f7f9fb;
            --on-bg: #dff3e4; --on-text: #14622a; --off-bg: #fbe3e1; --off-text: #9b2318; --warn-bg: #fff3d6; --warn-text: #7a5200;
        }
        @media (prefers-color-scheme: dark) {
            :root {
                --bg: #14171c; --surface: #1d2127; --text: #e6e9ee; --muted: #9aa5b4; --border: #333a45;
                --head: #1d4f80; --head-text: #f2f6fb; --row-alt: #232830;
                --on-bg: #17361f; --on-text: #7fdc98; --off-bg: #432320; --off-text: #ff9d92; --warn-bg: #3d3112; --warn-text: #f0c96a;
            }
        }
        * { box-sizing: border-box; }
        body { margin: 0; padding: 24px; background: var(--bg); color: var(--text); font: 14px/1.5 "Segoe UI", system-ui, -apple-system, sans-serif; }
        main { max-width: 1100px; margin: 0 auto; }
        h1 { margin: 0 0 4px; font-size: 26px; font-weight: 600; }
        h2 { margin: 32px 0 12px; padding: 8px 14px; background: var(--head); color: var(--head-text); border-radius: 6px; font-size: 18px; font-weight: 600; }
        h3 { margin: 18px 0 8px; font-size: 15px; font-weight: 600; }
        .lead { color: var(--muted); margin: 0 0 16px; white-space: pre-wrap; }
        .meta { width: 100%; border-collapse: collapse; background: var(--surface); border: 1px solid var(--border); border-radius: 6px; }
        .meta th { width: 160px; text-align: left; color: var(--muted); font-weight: 500; }
        .meta th, .meta td { padding: 6px 12px; border-bottom: 1px solid var(--border); }
        .meta tr:last-child th, .meta tr:last-child td { border-bottom: 0; }
        details { background: var(--surface); border: 1px solid var(--border); border-radius: 6px; margin: 10px 0; }
        summary { cursor: pointer; padding: 8px 14px; font-weight: 600; }
        details > .body { padding: 0 14px 12px; overflow-x: auto; }
        table.grid { width: 100%; border-collapse: collapse; }
        table.grid th { text-align: left; color: var(--muted); font-weight: 500; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; }
        table.grid th, table.grid td { padding: 6px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
        table.grid tbody tr:nth-child(even) { background: var(--row-alt); }
        table.grid td.policy { width: 38%; font-weight: 500; }
        table.grid td.state { width: 14%; white-space: nowrap; }
        .chip { display: inline-block; padding: 1px 10px; border-radius: 10px; font-size: 12px; font-weight: 600; }
        .on { background: var(--on-bg); color: var(--on-text); }
        .off { background: var(--off-bg); color: var(--off-text); }
        .warn { background: var(--warn-bg); color: var(--warn-text); }
        ul.values { list-style: none; margin: 6px 0 0; padding: 0; }
        ul.values li { padding: 2px 0; }
        ul.values .label { color: var(--muted); }
        table.sub { border-collapse: collapse; margin: 4px 0; }
        table.sub th, table.sub td { border: 1px solid var(--border); padding: 2px 8px; text-align: left; font-weight: normal; }
        table.sub th { color: var(--muted); }
        .empty { color: var(--muted); font-style: italic; margin: 8px 0; }
        code, .mono { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; word-break: break-all; }
        footer { margin-top: 32px; color: var(--muted); font-size: 12px; }
        """;

    public static partial string Build(GpoDocument gpo, AdmxCatalog catalog) {
        var sb = new StringBuilder();
        var version = typeof(HtmlReportWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HtmlReportWriter).Assembly.GetName().Version?.ToString() ?? "";
        var plus = version.IndexOf('+');
        if (plus >= 0) {
            version = version[..plus];
        }

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"color-scheme\" content=\"light dark\">\n");
        sb.Append("<title>").Append(E(gpo.Name)).Append(" - Settings report</title>\n<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n<main>\n");

        sb.Append("<h1>").Append(E(gpo.Name)).Append("</h1>\n");
        if (!string.IsNullOrWhiteSpace(gpo.Description)) {
            sb.Append("<p class=\"lead\">").Append(E(gpo.Description)).Append("</p>\n");
        }
        sb.Append("<table class=\"meta\">\n");
        MetaRow(sb, "Unique ID", gpo.Id.ToString("B").ToUpperInvariant());
        MetaRow(sb, "Created", gpo.Created.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        MetaRow(sb, "Modified", gpo.Modified.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        MetaRow(sb, "Generated by", ("ADMX2Reg " + version).Trim());
        MetaRow(sb, "Template language", catalog.Language);
        sb.Append("</table>\n");

        WriteScope(sb, "Computer Configuration", PolicyScope.Computer, gpo, catalog);
        WriteScope(sb, "User Configuration", PolicyScope.User, gpo, catalog);

        sb.Append("<footer>Generated by ADMX2Reg on ").Append(E(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append("</footer>\n");
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static void MetaRow(StringBuilder sb, string label, string value) =>
        sb.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>\n");

    // ------------------------------------------------------------------ scope

    private static void WriteScope(StringBuilder sb, string title, PolicyScope scope, GpoDocument gpo, AdmxCatalog catalog) {
        var settings = gpo.Settings(scope).Where(s => s.State != PolicyState.NotConfigured).ToList();
        var extra = gpo.ExtraRegistry(scope);

        sb.Append("<section>\n<h2>").Append(E(title)).Append("</h2>\n");
        if (settings.Count == 0 && extra.Count == 0) {
            sb.Append("<p class=\"empty\">No settings defined.</p>\n</section>\n");
            return;
        }

        if (settings.Count > 0) {
            sb.Append("<h3>Policies &gt; Administrative Templates</h3>\n");
            var known = settings
                .Select(s => (Setting: s, Policy: catalog.Policies.GetValueOrDefault(s.PolicyId)))
                .ToList();

            var groups = known.Where(k => k.Policy != null)
                .GroupBy(k => CategoryPath(k.Policy!, catalog))
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
            foreach (var group in groups) {
                sb.Append("<details open>\n<summary>").Append(E(group.Key.Length == 0 ? "(No category)" : group.Key)).Append("</summary>\n<div class=\"body\">\n");
                StartPolicyTable(sb);
                foreach (var (setting, policy) in group.OrderBy(k => k.Policy!.DisplayName, StringComparer.CurrentCultureIgnoreCase)) {
                    WritePolicyRow(sb, setting, policy!);
                }
                sb.Append("</tbody></table>\n</div>\n</details>\n");
            }

            var missing = known.Where(k => k.Policy == null).Select(k => k.Setting).ToList();
            if (missing.Count > 0) {
                sb.Append("<details open>\n<summary>Not found in loaded ADMX files</summary>\n<div class=\"body\">\n");
                StartPolicyTable(sb);
                foreach (var setting in missing) {
                    WriteMissingRow(sb, setting);
                }
                sb.Append("</tbody></table>\n</div>\n</details>\n");
            }
        }

        if (extra.Count > 0) {
            sb.Append("<h3>Extra Registry Settings</h3>\n<div class=\"body\">\n");
            sb.Append("<table class=\"grid\">\n<thead><tr><th>Key</th><th>Value name</th><th>Type</th><th>Data / action</th></tr></thead>\n<tbody>\n");
            foreach (var op in extra) {
                WriteExtraRow(sb, op);
            }
            sb.Append("</tbody></table>\n</div>\n");
        }
        sb.Append("</section>\n");
    }

    private static void StartPolicyTable(StringBuilder sb) =>
        sb.Append("<table class=\"grid\">\n<thead><tr><th>Policy</th><th>Setting</th><th>Comment</th></tr></thead>\n<tbody>\n");

    private static string CategoryPath(PolicyDefinition policy, AdmxCatalog catalog) {
        var category = policy.Category;
        if (category == null && policy.CategoryId != null) {
            catalog.Categories.TryGetValue(policy.CategoryId, out category);
        }
        var names = new List<string>();
        var guard = 0;
        while (category != null && guard++ < 50) {
            names.Add(category.DisplayName);
            var parent = category.Parent;
            if (parent == null && category.ParentId != null) {
                catalog.Categories.TryGetValue(category.ParentId, out parent);
            }
            category = parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static string StateChip(PolicyState state) => state == PolicyState.Enabled
        ? "<span class=\"chip on\">Enabled</span>"
        : "<span class=\"chip off\">Disabled</span>";

    // ------------------------------------------------------------------ policies

    private static void WritePolicyRow(StringBuilder sb, PolicySetting setting, PolicyDefinition policy) {
        sb.Append("<tr><td class=\"policy\">").Append(E(policy.DisplayName)).Append("</td><td class=\"state\">").Append(StateChip(setting.State));
        if (setting.State == PolicyState.Enabled) {
            WriteElementValues(sb, setting, policy);
        }
        sb.Append("</td><td>").Append(E(setting.Comment)).Append("</td></tr>\n");
    }

    private static void WriteElementValues(StringBuilder sb, PolicySetting setting, PolicyDefinition policy) {
        var rows = new List<string>();
        foreach (var element in policy.Elements) {
            if (!setting.Values.TryGetValue(element.Id, out var value)) {
                continue;
            }
            var label = ControlLabel(policy, element.Id);
            var rendered = RenderValue(element, value);
            if (rendered != null) {
                rows.Add("<li><span class=\"label\">" + E(label.TrimEnd().TrimEnd(':')) + ":</span> " + rendered + "</li>");
            }
        }
        if (rows.Count > 0) {
            sb.Append("<ul class=\"values\">").Append(string.Concat(rows)).Append("</ul>");
        }
    }

    private static string ControlLabel(PolicyDefinition policy, string elementId) {
        var control = policy.Presentation?.Controls.FirstOrDefault(c => string.Equals(c.RefId, elementId, StringComparison.OrdinalIgnoreCase) && c.Label.Length > 0);
        return control?.Label ?? elementId;
    }

    /// <summary>HTML for one element value, already encoded.</summary>
    private static string? RenderValue(PolicyElement element, PolicyElementValue value) {
        switch (element) {
            case BooleanElement when value.Boolean != null:
                return value.Boolean.Value ? "Enabled" : "Disabled";
            case EnumElement e when value.EnumIndex != null:
                var index = value.EnumIndex.Value;
                return E(index >= 0 && index < e.Items.Count ? e.Items[index].DisplayName : "(item " + index.ToString(CultureInfo.InvariantCulture) + ")");
            case MultiTextElement when value.Lines != null:
                return value.Lines.Count == 0 ? "<i>(empty)</i>" : string.Join("<br>", value.Lines.Select(E));
            case ListElement l when value.Entries != null:
                return RenderList(l, value.Entries);
            default:
                if (value.Number != null) {
                    return E(value.Number.Value.ToString(CultureInfo.InvariantCulture));
                }
                if (value.Text != null) {
                    return E(value.Text);
                }
                return RenderRaw(value);
        }
    }

    private static string RenderList(ListElement element, List<ListEntry> entries) {
        if (entries.Count == 0) {
            return "<i>(empty list)</i>";
        }
        var sb = new StringBuilder("<table class=\"sub\">");
        if (element.ExplicitValue) {
            sb.Append("<tr><th>Value name</th><th>Value</th></tr>");
            foreach (var entry in entries) {
                sb.Append("<tr><td>").Append(E(entry.Name)).Append("</td><td>").Append(E(entry.Value)).Append("</td></tr>");
            }
        } else {
            foreach (var entry in entries) {
                sb.Append("<tr><td>").Append(E(entry.Value)).Append("</td></tr>");
            }
        }
        return sb.Append("</table>").ToString();
    }

    /// <summary>Fallback when the element is unknown (policy definition changed): show whatever the value holds.</summary>
    private static string? RenderRaw(PolicyElementValue value) {
        if (value.Boolean != null) {
            return value.Boolean.Value ? "Enabled" : "Disabled";
        }
        if (value.Lines != null) {
            return string.Join("<br>", value.Lines.Select(E));
        }
        if (value.EnumIndex != null) {
            return "Item " + value.EnumIndex.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (value.Entries != null) {
            return string.Join("<br>", value.Entries.Select(e => E(e.Name.Length > 0 ? e.Name + " = " + e.Value : e.Value)));
        }
        return null;
    }

    private static void WriteMissingRow(StringBuilder sb, PolicySetting setting) {
        sb.Append("<tr><td class=\"policy\"><span class=\"mono\">").Append(E(setting.PolicyId)).Append("</span></td><td class=\"state\">")
            .Append(StateChip(setting.State)).Append("<br><span class=\"chip warn\">Template missing</span>");
        if (setting.State == PolicyState.Enabled && setting.Values.Count > 0) {
            sb.Append("<ul class=\"values\">");
            foreach (var (id, value) in setting.Values) {
                var rendered = value.Number != null ? E(value.Number.Value.ToString(CultureInfo.InvariantCulture))
                    : value.Text != null ? E(value.Text)
                    : RenderRaw(value);
                if (rendered != null) {
                    sb.Append("<li><span class=\"label\">").Append(E(id)).Append(":</span> ").Append(rendered).Append("</li>");
                }
            }
            sb.Append("</ul>");
        }
        sb.Append("</td><td>").Append(E(setting.Comment)).Append("</td></tr>\n");
    }

    // ------------------------------------------------------------------ extra registry

    private static void WriteExtraRow(StringBuilder sb, RegistryOperation op) {
        var hive = op.Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        var key = string.IsNullOrEmpty(op.Key) ? hive : hive + "\\" + op.Key;

        string name = "", type = "", data;
        switch (op.Kind) {
            case RegistryOperationKind.SetValue:
                name = op.ValueName is { Length: > 0 } ? op.ValueName : "(Default)";
                type = TypeName(op.Type);
                data = FormatData(op);
                break;
            case RegistryOperationKind.DeleteValue:
                name = op.ValueName is { Length: > 0 } ? op.ValueName : "(Default)";
                data = "Delete value";
                break;
            case RegistryOperationKind.DeleteAllValues:
                data = "Delete all values in key";
                break;
            case RegistryOperationKind.DeleteKey:
                data = "Delete key and all subkeys";
                break;
            default:
                data = "Create key";
                break;
        }

        sb.Append("<tr><td class=\"mono\">").Append(E(key)).Append("</td><td>").Append(E(name)).Append("</td><td>").Append(E(type))
            .Append("</td><td class=\"mono\">").Append(data.Contains('\n') ? string.Join("<br>", data.Split('\n').Select(E)) : E(data)).Append("</td></tr>\n");
    }

    private static string TypeName(RegValueType type) => type switch {
        RegValueType.String => "REG_SZ",
        RegValueType.ExpandString => "REG_EXPAND_SZ",
        RegValueType.DWord => "REG_DWORD",
        RegValueType.QWord => "REG_QWORD",
        RegValueType.MultiString => "REG_MULTI_SZ",
        RegValueType.Binary => "REG_BINARY",
        _ => "REG_NONE"
    };

    private static string FormatData(RegistryOperation op) {
        switch (op.Type) {
            case RegValueType.DWord: {
                var n = Convert.ToUInt32(op.Data, CultureInfo.InvariantCulture);
                return $"0x{n:x8} ({n})";
            }
            case RegValueType.QWord: {
                var n = Convert.ToUInt64(op.Data, CultureInfo.InvariantCulture);
                return $"0x{n:x16} ({n})";
            }
            case RegValueType.MultiString:
                return string.Join("\n", op.Data as string[] ?? []);
            case RegValueType.Binary: {
                var bytes = op.Data as byte[] ?? [];
                var text = string.Join(" ", bytes.Take(64).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
                return bytes.Length > 64 ? text + " ... (" + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes)" : text;
            }
            case RegValueType.None:
                return "";
            default:
                return op.Data as string ?? "";
        }
    }
}

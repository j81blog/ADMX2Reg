using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using ADMX2Reg.Core;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace ADMX2Reg.ViewModels;

/// <summary>One open GPO: its policy tree, the rows of the selected node, filters and the details of the selected policy.</summary>
public sealed partial class GpoTabViewModel : ObservableObject {
    private readonly MainViewModel _main;
    private readonly HashSet<string>[] _visibleCategories = [new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase)];

    // Configured-setting counts per category id, per scope; refreshed by RefreshCounts.
    private Dictionary<string, int>[]? _configuredCounts;
    private bool _rowsDirty = true;
    private int _rowsVersion = -1;

    public GpoTabViewModel(MainViewModel main, GpoItem item) {
        _main = main;
        Item = item;
    }

    public GpoItem Item { get; }
    public ObservableCollection<TreeNode> TreeRoots { get; } = [];

    /// <summary>True for the tab that is shown; inactive tabs only mark their rows as stale.</summary>
    public bool IsActive { get; private set; }

    // Tab headers and ItemsControl containers use ToString() as the automation name.
    public override string ToString() => Item.Name;

    [ObservableProperty]
    private bool _configuredOnly;

    [ObservableProperty]
    private TreeNode? _selectedNode;

    [ObservableProperty]
    private IReadOnlyList<PolicyRowVm> _rows = [];

    [ObservableProperty]
    private PolicyRowVm? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPolicyView))]
    private bool _isExtraView;

    [ObservableProperty]
    private IReadOnlyList<ExtraRow> _extraRows = [];

    [ObservableProperty]
    private string _listHint = "";

    [ObservableProperty]
    private bool _isSearchMode;

    [ObservableProperty]
    private IReadOnlyList<Crumb> _breadcrumb = [];

    [ObservableProperty]
    private string _detailsTitle = "";

    [ObservableProperty]
    private string _detailsSupported = "";

    [ObservableProperty]
    private string _detailsExplain = "";

    [ObservableProperty]
    private string _detailsRegistry = "";

    [ObservableProperty]
    private string _detailsPreview = "";

    [ObservableProperty]
    private bool _hasDetails;

    public bool IsPolicyView => !IsExtraView;

    public void SetActive(bool active) {
        IsActive = active;
        if (active && (_rowsDirty || _rowsVersion != _main.RowsVersion)) {
            RefreshRows();
        }
    }

    // ------------------------------------------------------------------ tree

    private HashSet<string> Visible(PolicyScope scope) => _visibleCategories[(int)scope];

    /// <summary>Recomputes the categories that hold at least one visible policy, per scope.</summary>
    private void ComputeVisibleCategories() {
        foreach (var set in _visibleCategories) {
            set.Clear();
        }
        var gpo = Item.Doc;
        var catalog = _main.Catalog;
        if (catalog == null) {
            return;
        }
        var filtering = _main.FilterVersion != null;
        var configured = new HashSet<string>[2];
        for (var scope = 0; scope < 2; scope++) {
            configured[scope] = filtering
                ? gpo.Settings((PolicyScope)scope).Where(s => s.State != PolicyState.NotConfigured).Select(s => s.PolicyId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
        }
        foreach (var policy in catalog.Policies.Values) {
            var matches = _main.MatchesVersionFilter(policy);
            for (var scope = 0; scope < 2; scope++) {
                if (!policy.AppliesTo((PolicyScope)scope) || !(matches || configured[scope].Contains(policy.Id))) {
                    continue;
                }
                for (var cat = _main.CategoryOf(policy); cat != null && _visibleCategories[scope].Add(cat.Id); cat = _main.ParentOf(cat)) {
                }
            }
        }
    }

    public void BuildTree(bool keepSelection = false) {
        var previous = keepSelection ? SelectedNode : null;
        var previousKind = previous?.Kind;
        var previousScope = previous?.Scope;
        var previousCategory = previous?.Category;
        var catalog = _main.Catalog;
        ComputeVisibleCategories();
        SelectedNode = null;
        TreeRoots.Clear();
        var gpo = Item.Doc;
        if (catalog != null) {
            foreach (var scope in new[] { PolicyScope.Computer, PolicyScope.User }) {
                var scopeNode = new TreeNode(this, NodeKind.Scope, scope,
                    scope == PolicyScope.Computer ? "Computer Configuration" : "User Configuration",
                    scope == PolicyScope.Computer ? SymbolRegular.Desktop24 : SymbolRegular.Person24);
                var admin = new TreeNode(this, NodeKind.AdminTemplates, scope, "Administrative Templates", SymbolRegular.Folder24);
                var scopeCopy = scope;
                admin.SetLazyChildren(() => CategoryNodes(catalog.RootCategories, scopeCopy));
                scopeNode.AddChild(admin);
                if (gpo.ExtraRegistry(scope).Count > 0) {
                    scopeNode.AddChild(new TreeNode(this, NodeKind.Extra, scope, "Extra Registry Settings", SymbolRegular.Database24));
                }
                TreeRoots.Add(scopeNode);
                scopeNode.IsExpanded = true;
            }
        }
        RefreshCounts();
        if (previousKind != null) {
            RestoreSelection(previousKind.Value, previousScope!.Value, previousCategory);
        }
        RefreshRows();
    }

    /// <summary>Reselects a node after a tree rebuild; a category that is gone moves to its nearest visible ancestor.</summary>
    private void RestoreSelection(NodeKind kind, PolicyScope scope, PolicyCategory? category) {
        var scopeNode = TreeRoots.FirstOrDefault(n => n.Scope == scope);
        if (scopeNode == null) {
            return;
        }
        if (kind == NodeKind.Scope) {
            scopeNode.IsSelected = true;
            return;
        }
        var admin = scopeNode.Children.FirstOrDefault(n => n.Kind == NodeKind.AdminTemplates);
        if (kind == NodeKind.Extra) {
            (scopeNode.Children.FirstOrDefault(n => n.Kind == NodeKind.Extra) ?? scopeNode).IsSelected = true;
            return;
        }
        if (admin == null) {
            return;
        }
        var visible = Visible(scope);
        var guard = 0;
        while (category != null && !visible.Contains(category.Id) && guard++ < 64) {
            category = _main.ParentOf(category);
        }
        var chain = new List<PolicyCategory>();
        for (var c = category; c != null && chain.Count < 64; c = _main.ParentOf(c)) {
            chain.Insert(0, c);
        }
        var node = admin;
        foreach (var cat in chain) {
            node.IsExpanded = true;
            var child = node.Children.FirstOrDefault(n => n.Category == cat);
            if (child == null) {
                break;
            }
            node = child;
        }
        node.IsSelected = true;
    }

    private IEnumerable<TreeNode> CategoryNodes(IEnumerable<PolicyCategory> categories, PolicyScope scope) {
        var visible = Visible(scope);
        foreach (var cat in categories.Where(c => visible.Contains(c.Id))) {
            var node = new TreeNode(this, NodeKind.Category, scope, cat.DisplayName, SymbolRegular.Folder24, cat);
            if (cat.Children.Any(c => visible.Contains(c.Id))) {
                node.SetLazyChildren(() => CategoryNodes(cat.Children, scope));
            }
            yield return node;
        }
    }

    /// <summary>Recomputes the "configured" counters on all loaded tree nodes.</summary>
    public void RefreshCounts() {
        var gpo = Item.Doc;
        var catalog = _main.Catalog;
        if (catalog == null) {
            return;
        }
        var counts = new Dictionary<string, int>[2];
        var totals = new int[2];
        foreach (var scope in new[] { PolicyScope.Computer, PolicyScope.User }) {
            var map = counts[(int)scope] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in gpo.Settings(scope)) {
                if (setting.State == PolicyState.NotConfigured) {
                    continue;
                }
                totals[(int)scope]++;
                if (!catalog.Policies.TryGetValue(setting.PolicyId, out var def)) {
                    continue;
                }
                var guard = 0;
                for (var cat = _main.CategoryOf(def); cat != null && guard++ < 64; cat = _main.ParentOf(cat)) {
                    map[cat.Id] = map.GetValueOrDefault(cat.Id) + 1;
                }
            }
        }

        void Apply(TreeNode node) {
            var s = (int)node.Scope;
            node.Configured = node.Kind switch {
                NodeKind.Category => counts[s].GetValueOrDefault(node.Category!.Id),
                NodeKind.Extra => gpo.ExtraRegistry(node.Scope).Count,
                _ => totals[s]
            };
            node.IsHidden = ConfiguredOnly && node.Kind == NodeKind.Category && node.Configured == 0;
            foreach (var child in node.Children.Where(c => !c.IsPlaceholder)) {
                Apply(child);
            }
        }

        _configuredCounts = counts;
        foreach (var root in TreeRoots) {
            Apply(root);
        }

        // A filtered-out selection moves to its nearest visible parent.
        var selected = SelectedNode;
        if (selected is { IsHidden: true }) {
            var target = selected.Parent;
            while (target is { IsHidden: true }) {
                target = target.Parent;
            }
            selected.IsSelected = false;
            if (target != null) {
                target.IsSelected = true;
            }
        }
    }

    /// <summary>Expands every branch that leads to configured settings (used when the filter is switched on).</summary>
    private void ExpandConfigured(TreeNode node) {
        var catalog = _main.Catalog;
        if (_configuredCounts == null || catalog == null || node.IsHidden) {
            return;
        }
        var map = _configuredCounts[(int)node.Scope];
        var childCategories = node.Kind switch {
            NodeKind.Scope => null,
            NodeKind.AdminTemplates => catalog.RootCategories,
            NodeKind.Category => node.Category!.Children,
            _ => null
        };
        if (node.Kind == NodeKind.Scope || childCategories?.Any(c => map.GetValueOrDefault(c.Id) > 0) == true) {
            node.IsExpanded = true;
        }
        foreach (var child in node.Children.Where(c => !c.IsPlaceholder).ToList()) {
            ExpandConfigured(child);
        }
    }

    public void OnNodeSelected(TreeNode node) {
        if (node.IsPlaceholder) {
            return;
        }
        SelectedNode = node;
    }

    partial void OnSelectedNodeChanged(TreeNode? value) => RefreshRows();

    partial void OnConfiguredOnlyChanged(bool value) {
        RefreshCounts();
        if (value) {
            foreach (var root in TreeRoots.ToList()) {
                ExpandConfigured(root);
            }
        }
        RefreshRows();
    }

    partial void OnSelectedRowChanged(PolicyRowVm? value) => UpdateDetails();

    // ------------------------------------------------------------------ rows / search

    private IReadOnlyList<Crumb> BuildBreadcrumb(bool search) {
        if (search) {
            return [new Crumb("Search results", true)];
        }
        if (ConfiguredOnly) {
            return [new Crumb("Configured settings", true)];
        }
        var node = SelectedNode;
        if (node == null) {
            return [];
        }
        var parts = new List<string>();
        for (var n = node; n != null; n = n.Parent) {
            if (n.Kind == NodeKind.AdminTemplates && n != node) {
                continue;
            }
            parts.Insert(0, n.Kind == NodeKind.Scope ? (n.Scope == PolicyScope.Computer ? "Computer" : "User") : n.Title);
        }
        return parts.Select((p, i) => new Crumb(p, i == parts.Count - 1)).ToList();
    }

    public void RefreshRows() {
        if (!IsActive) {
            _rowsDirty = true;
            return;
        }
        _rowsDirty = false;
        _rowsVersion = _main.RowsVersion;

        var gpo = Item.Doc;
        var catalog = _main.Catalog;
        var node = SelectedNode;
        var searchText = _main.SearchText;
        SelectedRow = null;

        IsExtraView = node?.Kind == NodeKind.Extra;
        if (IsExtraView) {
            ExtraRows = gpo.ExtraRegistry(node!.Scope).Select(op => new ExtraRow(op)).ToList();
            Rows = [];
            IsSearchMode = false;
            ListHint = "";
            Breadcrumb = BuildBreadcrumb(false);
            return;
        }
        ExtraRows = [];

        var terms = searchText.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var search = terms.Length > 0;
        // Flat list with a Category column: search results, or "Configured only" (which includes subcategories).
        IsSearchMode = search || ConfiguredOnly;
        Breadcrumb = BuildBreadcrumb(search);

        if (catalog == null) {
            Rows = [];
            ListHint = _main.IsLoading ? "" : "Administrative templates are not loaded.";
            return;
        }

        var flat = IsSearchMode;
        if (!flat && node == null) {
            Rows = [];
            ListHint = "Select a category in the tree, or type in the search box.";
            return;
        }

        var scopes = node != null ? new[] { node.Scope } : [PolicyScope.Computer, PolicyScope.User];
        var list = new List<PolicyRowVm>();
        foreach (var scope in scopes) {
            var map = new Dictionary<string, PolicySetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in gpo.Settings(scope)) {
                map[s.PolicyId] = s;
            }

            IEnumerable<PolicyDefinition> source;
            if (flat) {
                source = catalog.Policies.Values.Where(p => p.AppliesTo(scope));
                if (!search && node?.Kind == NodeKind.Category) {
                    source = source.Where(p => _main.IsInCategory(p, node.Category!));
                }
            } else if (node!.Kind == NodeKind.Category) {
                source = node.Category!.Policies.Where(p => p.AppliesTo(scope));
            } else {
                source = catalog.Policies.Values.Where(p => p.AppliesTo(scope) && _main.CategoryOf(p) == null);
            }

            foreach (var policy in source) {
                if (search) {
                    var haystack = _main.Haystack(policy);
                    if (!terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase))) {
                        continue;
                    }
                }
                map.TryGetValue(policy.Id, out var setting);
                if (setting is { State: PolicyState.NotConfigured }) {
                    setting = null;
                }
                if (ConfiguredOnly && setting == null) {
                    continue;
                }
                if (setting == null && !_main.MatchesVersionFilter(policy)) {
                    continue;
                }
                var path = _main.CategoryPath(_main.CategoryOf(policy));
                if (flat && scopes.Length > 1) {
                    path = (scope == PolicyScope.Computer ? "Computer" : "User") + " > " + path;
                }
                list.Add(new PolicyRowVm(policy, scope, setting, path, setting == null ? null : _main.VersionWarningFor(policy)));
            }
        }

        if (flat) {
            // Rank by relevance: exact name, name starts with the query, all terms in the name, other matches.
            var query = searchText.Trim();
            int Rank(PolicyRowVm r) =>
                !search ? 0
                : r.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
                : r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
                : terms.All(t => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 2
                : 3;
            list = [.. list.OrderBy(Rank).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Scope)];
        }
        Rows = list;
        ListHint = list.Count == 0
            ? (search ? "No settings match your search."
                : ConfiguredOnly ? "No configured settings."
                : node?.Kind == NodeKind.Category ? "No settings in this category."
                : "Select a category in the tree to see its settings.")
            : "";
    }

    // ------------------------------------------------------------------ details

    public void UpdateDetails() {
        var row = SelectedRow;
        HasDetails = row != null;
        if (row == null) {
            DetailsTitle = DetailsSupported = DetailsExplain = DetailsRegistry = DetailsPreview = "";
            return;
        }
        var def = row.Definition;
        DetailsTitle = def.DisplayName;
        DetailsSupported = string.IsNullOrWhiteSpace(def.SupportedOn) ? "" : def.SupportedOn;
        DetailsExplain = def.ExplainText;
        var hive = row.Scope == PolicyScope.Computer ? "HKLM" : "HKCU";
        var reg = new StringBuilder();
        reg.Append(hive).Append('\\').Append(def.Key);
        if (!string.IsNullOrEmpty(def.ValueName)) {
            reg.Append("  ->  ").Append(def.ValueName);
        }
        foreach (var el in def.Elements.Where(e => !string.IsNullOrEmpty(e.ValueName))) {
            reg.AppendLine().Append("Element ").Append(el.Id).Append(": ");
            if (!string.IsNullOrEmpty(el.Key)) {
                reg.Append(hive).Append('\\').Append(el.Key).Append("  ->  ");
            }
            reg.Append(el.ValueName);
        }
        DetailsRegistry = reg.ToString();
        DetailsPreview = BuildPreview(row);
    }

    private static string BuildPreview(PolicyRowVm row) {
        try {
            if (row.Setting == null || row.Setting.State == PolicyState.NotConfigured) {
                return "; Not configured: this policy makes no registry changes.";
            }
            var ops = PolicyEvaluator.Evaluate(row.Definition, row.Setting, row.Scope);
            using var writer = new StringWriter();
            RegFileWriter.Write(ops, writer);
            return writer.ToString();
        } catch (Exception ex) {
            return "; Preview failed: " + ex.Message;
        }
    }
}

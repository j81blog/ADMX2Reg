using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ADMX2Reg.Core;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;
using ADMX2Reg.Services;
using ADMX2Reg.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RegistryHive = ADMX2Reg.Core.Registry.RegistryHive;

namespace ADMX2Reg.ViewModels;

public sealed partial class MainViewModel : ObservableObject {
    private readonly AppSettings _settings;
    private readonly Dictionary<string, string> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _previewTimer;
    private Dictionary<string, string>? _haystack;
    private GpoStore _store;
    private CancellationTokenSource? _loadCts;
    private List<string> _gpoLoadErrors = [];
    private bool _suppressVersionFilter;
    private bool _restoring;
    private ExportPreviewResult? _preview;

    public MainViewModel(AppSettings settings) {
        _settings = settings;
        _includeUnversioned = settings.IncludeUnversioned;
        _isSidePanelVisible = settings.SidePanelVisible;
        _isRailExpanded = settings.RailExpanded;
        _isBottomExpanded = !settings.BottomPanelCollapsed;
        _store = new GpoStore(settings.GpoFolderPath);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        Gpos.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoGpos));
        OpenTabs.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(HasNoTabs));
        };
        _searchTimer.Tick += (_, _) => {
            _searchTimer.Stop();
            RowsVersion++;
            ActiveTab?.RefreshRows();
        };
        _previewTimer.Tick += (_, _) => RefreshPreviewNow();
    }

    public AppSettings Settings => _settings;
    public ObservableCollection<GpoItem> Gpos { get; } = [];
    public ObservableCollection<GpoTabViewModel> OpenTabs { get; } = [];

    /// <summary>Bumped whenever something that changes every tab's rows (search, version filter, catalog) changes.</summary>
    public int RowsVersion { get; private set; }

    /// <summary>The GPO highlighted in the side panel. Selecting it opens (or activates) its tab.</summary>
    [ObservableProperty]
    private GpoItem? _selectedGpo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGpo))]
    [NotifyPropertyChangedFor(nameof(CanExportPreview))]
    private GpoTabViewModel? _activeTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyCountText))]
    [NotifyPropertyChangedFor(nameof(LanguageText))]
    [NotifyPropertyChangedFor(nameof(WarningText))]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private AdmxCatalog? _catalog;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private IReadOnlyList<WindowsVersionOption> _windowsVersionOptions = [AllVersions];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersionSelected))]
    [NotifyPropertyChangedFor(nameof(HasFilter))]
    [NotifyPropertyChangedFor(nameof(FilterText))]
    private WindowsVersionOption? _selectedWindowsVersion = AllVersions;

    [ObservableProperty]
    private bool _includeUnversioned = true;

    [ObservableProperty]
    private bool _isSidePanelVisible = true;

    /// <summary>The left rail shows labels next to its icons.</summary>
    [ObservableProperty]
    private bool _isRailExpanded;

    [ObservableProperty]
    private bool _isBottomExpanded = true;

    /// <summary>0 = Setting, 1 = .reg, 2 = PowerShell, 3 = PowerShell (minimal), 4 = Registry.pol.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingTab))]
    [NotifyPropertyChangedFor(nameof(IsExportTab))]
    [NotifyPropertyChangedFor(nameof(CanCopyPreview))]
    [NotifyPropertyChangedFor(nameof(CanExportPreview))]
    [NotifyPropertyChangedFor(nameof(BottomTabHelp))]
    private int _bottomTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportPreview))]
    private bool _includeComputer = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportPreview))]
    private bool _includeUser = true;

    [ObservableProperty]
    private string _previewText = "";

    [ObservableProperty]
    private string _previewLineNumbers = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewWarnings))]
    private int _previewWarningCount;

    [ObservableProperty]
    private string _previewWarningsText = "";

    private static readonly WindowsVersionOption AllVersions = new(null, "All versions");

    public bool IsVersionSelected => SelectedWindowsVersion?.Version != null;
    public bool HasFilter => IsVersionSelected;
    public string FilterText => SelectedWindowsVersion?.Version == null ? "" : $"Filter: {SelectedWindowsVersion.Text}";
    public bool HasGpo => ActiveTab != null;
    public bool HasTabs => OpenTabs.Count > 0;
    public bool HasNoTabs => OpenTabs.Count == 0;
    public bool HasNoGpos => Gpos.Count == 0;
    public bool HasLoadError => LoadError != null;
    public bool HasWarnings => (Catalog?.Warnings.Count ?? 0) > 0;
    public bool IsSettingTab => BottomTabIndex == 0;
    public bool IsExportTab => BottomTabIndex > 0;

    /// <summary>Help text for the selected bottom panel view.</summary>
    public string BottomTabHelp => BottomTabIndex switch {
        1 => "Windows Registry Editor file, saved as UTF-16 like regedit. Import it with regedit or reg import. "
            + "Settings under User Configuration apply to the user who imports the file.\n\n"
            + "List policies replace all values in their key. A .reg file can only do that by deleting and recreating the key, "
            + "which also removes its subkeys.",
        2 => "Standalone script for Windows PowerShell 5.1 and PowerShell 7. It only changes values that differ, "
            + "supports -WhatIf and -Verbose, and prints a summary.\n\n"
            + "HKLM settings need an elevated session. HKCU settings apply to the user running the script.",
        3 => "A flat list of New-Item, New-ItemProperty and Remove-ItemProperty commands, without checks or logging. "
            + "Easy to read or to paste into your own scripts. HKLM settings need an elevated session.",
        4 => "Group Policy registry file, written as Machine\\Registry.pol and User\\Registry.pol. "
            + "Apply it with LGPO.exe (/m or /u), or copy it into the Machine or User folder of a GPO in SYSVOL.\n\n"
            + "The file is binary; this preview lists its entries in readable form.",
        _ => "Details of the selected policy: where it is supported, which registry values it writes, and its explanation from the ADMX file. "
            + "The other tabs preview the export of the whole GPO."
    };
    public bool HasPreviewWarnings => PreviewWarningCount > 0;
    public bool CanCopyPreview => BottomTabIndex is >= 1 and <= 3;
    public bool CanExportPreview => ActiveTab != null && (BottomTabIndex == 0 || IncludeComputer || IncludeUser);
    public string PolicyCountText => Catalog == null ? "Policies: none" : $"Policies: {Catalog.Policies.Count}";
    public string LanguageText => Catalog == null ? "Language: n/a" : $"Language: {Catalog.Language}";
    public string WarningText => $"Warnings: {Catalog?.Warnings.Count ?? 0}";
    public string PathText => $"PolicyDefinitions: {_settings.PolicyDefinitionsFullPath}";
    public string GpoFolderText => $"GPO folder: {_settings.GpoFolderPath}";

    // ------------------------------------------------------------------ startup / loading

    public async Task InitializeAsync() {
        LoadGpos(_settings.OpenGpoIds, _settings.ActiveGpoId);
        await LoadCatalogAsync();
    }

    /// <summary>Reloads the GPO list and reopens the tabs of the given ids (null = open the first GPO).</summary>
    private void LoadGpos(IReadOnlyList<Guid>? openIds, Guid? activeId) {
        _restoring = true;
        try {
            ActiveTab = null;
            OpenTabs.Clear();
            _store = new GpoStore(_settings.GpoFolderPath);
            _gpoLoadErrors = [];
            Gpos.Clear();
            try {
                Directory.CreateDirectory(_settings.GpoFolderPath);
                var docs = _store.LoadAll(out var errors);
                _gpoLoadErrors = errors;
                foreach (var doc in docs.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)) {
                    Gpos.Add(new GpoItem(doc));
                }
                if (errors.Count > 0) {
                    Dialogs.Error("Some GPOs could not be loaded", string.Join(Environment.NewLine, errors));
                }
            } catch (Exception ex) {
                _gpoLoadErrors = [ex.Message];
                Dialogs.Error("Could not load GPOs", $"Folder: {_settings.GpoFolderPath}{Environment.NewLine}{ex.Message}");
            }

            if (openIds == null) {
                if (Gpos.FirstOrDefault() is { } first) {
                    OpenOrActivate(first);
                }
            } else {
                foreach (var id in openIds) {
                    if (Gpos.FirstOrDefault(g => g.Doc.Id == id) is { } item) {
                        OpenOrActivate(item);
                    }
                }
                var active = OpenTabs.FirstOrDefault(t => t.Item.Doc.Id == activeId) ?? OpenTabs.FirstOrDefault();
                ActiveTab = active;
            }
            OnPropertyChanged(nameof(GpoFolderText));
        } finally {
            _restoring = false;
        }
        PersistTabs();
    }

    private async Task LoadCatalogAsync() {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        LoadError = null;
        LoadingText = "Loading administrative templates...";
        OnPropertyChanged(nameof(PathText));
        try {
            var progress = new Progress<string>(text => {
                if (_loadCts == cts) {
                    LoadingText = text;
                }
            });
            var path = _settings.PolicyDefinitionsFullPath;
            var language = _settings.Language;
            var catalog = await Task.Run(() => AdmxLoader.LoadAsync(path, language, progress, cts.Token), cts.Token);
            if (_loadCts != cts) {
                return;
            }
            SetCatalog(catalog);
            if (catalog.Policies.Count == 0) {
                LoadError = $"No policy definitions were found in '{path}'.";
            }
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            if (_loadCts != cts) {
                return;
            }
            SetCatalog(null);
            LoadError = $"Failed to load administrative templates from '{_settings.PolicyDefinitionsFullPath}': {ex.Message}";
        } finally {
            if (_loadCts == cts) {
                IsLoading = false;
            }
        }
    }

    private void SetCatalog(AdmxCatalog? catalog) {
        Catalog = catalog;
        _haystack = null;
        _pathCache.Clear();
        _suppressVersionFilter = true;
        try {
            var options = new List<WindowsVersionOption> { AllVersions };
            if (catalog != null) {
                options.AddRange(catalog.WindowsVersions.Select(v => new WindowsVersionOption(v, ShortName(v))));
            }
            WindowsVersionOptions = options;
            SelectedWindowsVersion = options.FirstOrDefault(o => o.Version != null && o.Version.Name == _settings.WindowsVersionFilter) ?? AllVersions;
        } finally {
            _suppressVersionFilter = false;
        }
        RebuildAllTabs(keepSelection: false);
        SchedulePreview();
    }

    private void RebuildAllTabs(bool keepSelection) {
        RowsVersion++;
        foreach (var tab in OpenTabs.ToList()) {
            tab.BuildTree(keepSelection);
        }
    }

    internal WindowsVersion? FilterVersion => SelectedWindowsVersion?.Version;

    /// <summary>True when the policy passes the Windows version filter (always true without a selected version).</summary>
    internal bool MatchesVersionFilter(PolicyDefinition policy) {
        if (FilterVersion is not { } version) {
            return true;
        }
        var supported = policy.SupportsWindows(version.Index);
        return supported ?? IncludeUnversioned;
    }

    /// <summary>Tooltip for a configured policy that the Windows version filter would hide, otherwise null.</summary>
    internal string? VersionWarningFor(PolicyDefinition policy) =>
        FilterVersion is { } version && !MatchesVersionFilter(policy)
            ? $"Not supported on {ShortName(version)} according to the ADMX supportedOn information."
            : null;

    partial void OnSelectedWindowsVersionChanged(WindowsVersionOption? value) {
        if (_suppressVersionFilter || value == null) {
            return;
        }
        _settings.WindowsVersionFilter = value.Version?.Name;
        _settings.Save();
        RebuildAllTabs(keepSelection: true);
    }

    partial void OnIncludeUnversionedChanged(bool value) {
        if (_suppressVersionFilter) {
            return;
        }
        _settings.IncludeUnversioned = value;
        _settings.Save();
        if (FilterVersion != null) {
            RebuildAllTabs(keepSelection: true);
        }
    }

    // ADML names read "Windows 11 operating systems"; the suffix adds nothing in a version picker.
    private static string ShortName(WindowsVersion version) {
        const string suffix = " operating systems";
        return version.DisplayName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? version.DisplayName[..^suffix.Length]
            : version.DisplayName;
    }

    internal PolicyCategory? CategoryOf(PolicyDefinition policy) =>
        policy.Category ?? (policy.CategoryId != null && Catalog != null && Catalog.Categories.TryGetValue(policy.CategoryId, out var c) ? c : null);

    internal bool IsInCategory(PolicyDefinition policy, PolicyCategory category) {
        var guard = 0;
        for (var cat = CategoryOf(policy); cat != null && guard++ < 64; cat = ParentOf(cat)) {
            if (cat == category) {
                return true;
            }
        }
        return false;
    }

    internal PolicyCategory? ParentOf(PolicyCategory cat) =>
        cat.Parent ?? (cat.ParentId != null && Catalog != null && Catalog.Categories.TryGetValue(cat.ParentId, out var p) ? p : null);

    internal string CategoryPath(PolicyCategory? cat) {
        if (cat == null) {
            return "";
        }
        if (_pathCache.TryGetValue(cat.Id, out var cached)) {
            return cached;
        }
        var parts = new List<string>();
        var guard = 0;
        for (var c = cat; c != null && guard++ < 64; c = ParentOf(c)) {
            parts.Add(c.DisplayName);
        }
        parts.Reverse();
        return _pathCache[cat.Id] = string.Join(" > ", parts);
    }

    internal string Haystack(PolicyDefinition p) {
        _haystack ??= [];
        if (!_haystack.TryGetValue(p.Id, out var text)) {
            var sb = new StringBuilder();
            sb.Append(p.DisplayName).Append('\n').Append(p.ExplainText).Append('\n').Append(p.Key).Append('\n').Append(p.ValueName);
            foreach (var el in p.Elements) {
                sb.Append('\n').Append(el.ValueName).Append('\n').Append(el.Key);
            }
            _haystack[p.Id] = text = sb.ToString();
        }
        return text;
    }

    // ------------------------------------------------------------------ tabs

    partial void OnSelectedGpoChanged(GpoItem? value) {
        if (value != null) {
            OpenOrActivate(value);
        }
    }

    partial void OnActiveTabChanged(GpoTabViewModel? oldValue, GpoTabViewModel? newValue) {
        oldValue?.SetActive(false);
        newValue?.SetActive(true);
        SelectedGpo = newValue?.Item;
        SchedulePreview();
        PersistTabs();
    }

    partial void OnSearchTextChanged(string value) {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OpenOrActivate(GpoItem item) {
        var tab = OpenTabs.FirstOrDefault(t => t.Item == item);
        if (tab == null) {
            tab = new GpoTabViewModel(this, item);
            OpenTabs.Add(tab);
            tab.BuildTree();
        }
        ActiveTab = tab;
        PersistTabs();
    }

    public void CloseTab(GpoTabViewModel tab) {
        var index = OpenTabs.IndexOf(tab);
        if (index < 0) {
            return;
        }
        var wasActive = tab == ActiveTab;
        OpenTabs.Remove(tab);
        if (wasActive) {
            ActiveTab = OpenTabs.Count == 0 ? null : OpenTabs[Math.Min(index, OpenTabs.Count - 1)];
        }
        PersistTabs();
    }

    /// <summary>Activates the next (+1) or previous (-1) tab, wrapping around.</summary>
    public void CycleTab(int delta) {
        if (OpenTabs.Count < 2 || ActiveTab == null) {
            return;
        }
        var index = (OpenTabs.IndexOf(ActiveTab) + delta + OpenTabs.Count) % OpenTabs.Count;
        ActiveTab = OpenTabs[index];
    }

    private void PersistTabs() {
        if (_restoring) {
            return;
        }
        _settings.OpenGpoIds = OpenTabs.Select(t => t.Item.Doc.Id).ToList();
        _settings.ActiveGpoId = ActiveTab?.Item.Doc.Id;
        _settings.Save();
    }

    [RelayCommand]
    private void OpenGpo() {
        if (SelectedGpo is { } item) {
            OpenOrActivate(item);
        }
    }

    [RelayCommand]
    private void CloseActiveTab() {
        if (ActiveTab is { } tab) {
            CloseTab(tab);
        }
    }

    [RelayCommand]
    private void CloseTabOf(GpoTabViewModel? tab) {
        if (tab != null) {
            CloseTab(tab);
        }
    }

    [RelayCommand]
    private void CloseOtherTabs() {
        var keep = ActiveTab;
        foreach (var tab in OpenTabs.Where(t => t != keep).ToList()) {
            OpenTabs.Remove(tab);
        }
        PersistTabs();
    }

    // ------------------------------------------------------------------ side and bottom panel

    [RelayCommand]
    private void ToggleRail() => IsRailExpanded = !IsRailExpanded;

    [RelayCommand]
    private static void About() => new Views.AboutWindow().ShowModal();

    partial void OnIsRailExpandedChanged(bool value) {
        _settings.RailExpanded = value;
        _settings.Save();
    }

    partial void OnIsSidePanelVisibleChanged(bool value) {
        _settings.SidePanelVisible = value;
        _settings.Save();
    }

    partial void OnIsBottomExpandedChanged(bool value) {
        _settings.BottomPanelCollapsed = !value;
        _settings.Save();
        SchedulePreview();
    }

    [RelayCommand]
    private void ToggleBottomPanel() => IsBottomExpanded = !IsBottomExpanded;

    partial void OnBottomTabIndexChanged(int value) {
        if (!IsBottomExpanded) {
            IsBottomExpanded = true;
        }
        RefreshPreviewNow();
    }

    partial void OnIncludeComputerChanged(bool value) => SchedulePreview();

    partial void OnIncludeUserChanged(bool value) => SchedulePreview();

    private ExportFormat PreviewFormat => BottomTabIndex switch {
        2 => ExportFormat.PowerShell,
        3 => ExportFormat.PowerShellMinimal,
        4 => ExportFormat.RegistryPol,
        _ => ExportFormat.Reg
    };

    /// <summary>Rebuilds the export preview shortly after the last change, so typing and editing stay responsive.</summary>
    private void SchedulePreview() {
        if (BottomTabIndex == 0 || !IsBottomExpanded) {
            return;
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshPreviewNow() {
        _previewTimer.Stop();
        var tab = ActiveTab;
        if (BottomTabIndex == 0 || !IsBottomExpanded || tab == null || Catalog == null) {
            _preview = null;
            PreviewText = "";
            PreviewLineNumbers = "";
            PreviewWarningCount = 0;
            PreviewWarningsText = "";
            return;
        }
        var preview = _preview = ExportPreviewBuilder.Build(tab.Item.Doc, Catalog, IncludeComputer, IncludeUser, PreviewFormat);
        PreviewText = preview.Text;
        var lines = preview.Text.Count(c => c == '\n') + 1;
        PreviewLineNumbers = string.Join('\n', Enumerable.Range(1, lines));
        PreviewWarningCount = preview.Warnings.Count;
        PreviewWarningsText = string.Join(Environment.NewLine, preview.Warnings);
    }

    [RelayCommand]
    private void CopyPreview() {
        if (!CanCopyPreview || string.IsNullOrEmpty(PreviewText)) {
            return;
        }
        try {
            Clipboard.SetText(PreviewText);
        } catch (Exception ex) {
            Dialogs.Error("Copy failed", ex.Message);
        }
    }

    /// <summary>Saves the format of the active preview tab directly; on the Setting tab it opens the Export window.</summary>
    [RelayCommand]
    private void ExportPreview() {
        if (ActiveTab is not { } tab) {
            return;
        }
        if (BottomTabIndex == 0) {
            Export();
            return;
        }
        if (Catalog == null) {
            Dialogs.Error("Export", "The administrative templates are not loaded.");
            return;
        }
        RefreshPreviewNow();
        try {
            ExportPreviewBuilder.Save(tab.Item.Doc, _preview!, PreviewFormat, _settings);
        } catch (Exception ex) {
            Dialogs.Error("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ShowPreviewWarnings() {
        var lines = _preview?.Warnings ?? [];
        new ListWindow("Compile warnings", $"{lines.Count} warning(s) while compiling this GPO.", lines, false).ShowModal();
    }

    // ------------------------------------------------------------------ editing policies

    /// <summary>Applies a setting to the in-memory GPO and the row. Call <see cref="Commit"/> afterwards to save.</summary>
    public void SetSetting(PolicyRowVm row, PolicySetting? setting) {
        var gpo = ActiveTab?.Item.Doc;
        if (gpo == null) {
            return;
        }
        var list = gpo.Settings(row.Scope);
        list.RemoveAll(s => string.Equals(s.PolicyId, row.Definition.Id, StringComparison.OrdinalIgnoreCase));
        if (setting != null && setting.State != PolicyState.NotConfigured) {
            list.Add(setting);
        } else {
            setting = null;
        }
        row.Update(setting);
    }

    public void Commit() {
        if (ActiveTab is not { } tab) {
            return;
        }
        SaveGpo(tab.Item);
        tab.RefreshCounts();
        tab.UpdateDetails();
    }

    private void SaveGpo(GpoItem item) {
        try {
            Directory.CreateDirectory(_settings.GpoFolderPath);
            _store.Save(item.Doc);
        } catch (Exception ex) {
            Dialogs.Error("Could not save the GPO", ex.Message);
        }
        item.Refresh();
        if (ActiveTab?.Item == item) {
            SchedulePreview();
        }
    }

    [RelayCommand]
    private void EditSelected() {
        if (ActiveTab is not { SelectedRow: { } row } tab) {
            return;
        }
        var rows = tab.Rows;
        var window = new PolicyEditorWindow(rows, rows.ToList().IndexOf(row), this);
        window.ShowModal();
        if (tab.ConfiguredOnly) {
            tab.RefreshRows();
        }
    }

    public void ClearSettings(IReadOnlyList<PolicyRowVm> rows) {
        var configured = rows.Where(r => r.IsConfigured).ToList();
        if (configured.Count == 0) {
            return;
        }
        var message = configured.Count == 1
            ? $"Set '{configured[0].Name}' back to Not configured?"
            : $"Set {configured.Count} settings back to Not configured?";
        if (!Dialogs.Confirm("Remove setting", message, "Remove")) {
            return;
        }
        foreach (var row in configured) {
            SetSetting(row, null);
        }
        Commit();
        if (ActiveTab is { ConfiguredOnly: true } tab) {
            tab.RefreshRows();
        }
    }

    public void CopyToGpo(IReadOnlyList<PolicyRowVm> rows) {
        var configured = rows.Where(r => r.IsConfigured).ToList();
        var current = ActiveTab?.Item;
        var targets = Gpos.Where(g => g != current).ToList();
        if (configured.Count == 0) {
            Dialogs.Info("Copy to GPO", "Select one or more configured settings first.");
            return;
        }
        if (targets.Count == 0) {
            Dialogs.Info("Copy to GPO", "There is no other GPO to copy to. Create one first.");
            return;
        }
        var window = new ListWindow("Copy to GPO", $"Copy {configured.Count} setting(s) to:", targets.Select(t => t.Name), true, "Copy");
        if (window.ShowModal() != true) {
            return;
        }
        var target = targets[window.SelectedIndex];
        foreach (var row in configured) {
            var copy = JsonSerializer.Deserialize<PolicySetting>(JsonSerializer.Serialize(row.Setting))!;
            Upsert(target.Doc.Settings(row.Scope), copy);
        }
        SaveGpo(target);
        OpenTabs.FirstOrDefault(t => t.Item == target)?.BuildTree(keepSelection: true);
        Dialogs.Info("Copy to GPO", $"Copied {configured.Count} setting(s) to '{target.Name}'.");
    }

    private static void Upsert(List<PolicySetting> list, PolicySetting setting) {
        list.RemoveAll(s => string.Equals(s.PolicyId, setting.PolicyId, StringComparison.OrdinalIgnoreCase));
        list.Add(setting);
    }

    public void RemoveExtra(IReadOnlyList<ExtraRow> rows) {
        var tab = ActiveTab;
        var node = tab?.SelectedNode;
        if (tab == null || node == null || rows.Count == 0) {
            return;
        }
        if (!Dialogs.Confirm("Remove registry settings", $"Remove {rows.Count} extra registry setting(s) from this GPO?", "Remove")) {
            return;
        }
        var list = tab.Item.Doc.ExtraRegistry(node.Scope);
        foreach (var row in rows) {
            list.Remove(row.Operation);
        }
        Commit();
        tab.RefreshRows();
    }

    // ------------------------------------------------------------------ GPO commands

    [RelayCommand]
    private void NewGpo() {
        var window = new GpoEditWindow("New GPO", "New Group Policy Object", null);
        if (window.ShowModal() != true) {
            return;
        }
        AddGpo(new GpoDocument { Name = window.GpoName, Description = window.GpoDescription });
    }

    private void AddGpo(GpoDocument doc) {
        var item = new GpoItem(doc);
        var index = 0;
        while (index < Gpos.Count && string.Compare(Gpos[index].Name, doc.Name, StringComparison.CurrentCultureIgnoreCase) < 0) {
            index++;
        }
        Gpos.Insert(index, item);
        SaveGpo(item);
        SelectedGpo = item;
    }

    [RelayCommand]
    private void RenameGpo() {
        if (ActiveTab?.Item is not { } item) {
            return;
        }
        var window = new GpoEditWindow("Rename GPO", item.Doc.Name, item.Doc.Description);
        if (window.ShowModal() != true) {
            return;
        }
        item.Doc.Name = window.GpoName;
        item.Doc.Description = window.GpoDescription;
        SaveGpo(item);
        Gpos.Remove(item);
        var index = 0;
        while (index < Gpos.Count && string.Compare(Gpos[index].Name, item.Name, StringComparison.CurrentCultureIgnoreCase) < 0) {
            index++;
        }
        Gpos.Insert(index, item);
        SelectedGpo = item;
    }

    [RelayCommand]
    private void DuplicateGpo() {
        if (ActiveTab?.Item is not { } item) {
            return;
        }
        try {
            AddGpo(GpoStore.Duplicate(item.Doc));
        } catch (Exception ex) {
            Dialogs.Error("Could not duplicate the GPO", ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteGpo() {
        if (ActiveTab is not { } tab) {
            return;
        }
        var item = tab.Item;
        if (!Dialogs.Confirm("Delete GPO", $"Delete the Group Policy Object '{item.Name}'? This cannot be undone.", "Delete")) {
            return;
        }
        try {
            _store.Delete(item.Doc);
        } catch (Exception ex) {
            Dialogs.Error("Could not delete the GPO", ex.Message);
            return;
        }
        CloseTab(tab);
        Gpos.Remove(item);
    }

    [RelayCommand]
    private async Task ImportAsync() {
        var dialog = new OpenFileDialog {
            Title = "Import registry settings",
            Filter = "Registry files (*.reg;*.pol)|*.reg;*.pol|Registry script (*.reg)|*.reg|Registry.pol (*.pol)|*.pol|All files (*.*)|*.*",
            InitialDirectory = FolderOrNull(_settings.LastImportFolder) ?? ""
        };
        if (dialog.ShowDialog() != true) {
            return;
        }
        _settings.LastImportFolder = Path.GetDirectoryName(dialog.FileName);
        _settings.Save();
        if (Catalog == null) {
            Dialogs.Error("Import", "The administrative templates are not loaded, so settings cannot be matched to policies.");
            return;
        }

        var catalog = Catalog;
        var file = dialog.FileName;
        var warnings = new List<string>();
        try {
            IReadOnlyList<RegistryOperation> ops;
            if (string.Equals(Path.GetExtension(file), ".pol", StringComparison.OrdinalIgnoreCase)) {
                var folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
                RegistryHive hive;
                if (string.Equals(folder, "Machine", StringComparison.OrdinalIgnoreCase)) {
                    hive = RegistryHive.LocalMachine;
                } else if (string.Equals(folder, "User", StringComparison.OrdinalIgnoreCase)) {
                    hive = RegistryHive.CurrentUser;
                } else {
                    var answer = Dialogs.Show("Import Registry.pol", "Which configuration does this Registry.pol belong to?", "Computer", "User", "Cancel");
                    if (answer is < 0 or 2) {
                        return;
                    }
                    hive = answer == 0 ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                }
                ops = await Task.Run(() => {
                    using var stream = File.OpenRead(file);
                    return PolFileReader.Read(stream, hive);
                });
            } else {
                ops = await Task.Run(() => {
                    using var reader = new StreamReader(file, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
                    return RegFileReader.Read(reader, warnings);
                });
            }
            var match = await Task.Run(() => PolicyMatcher.Match(ops, catalog));

            var window = new ImportWindow(Path.GetFileNameWithoutExtension(file), ActiveTab?.Item.Name, match, warnings);
            if (window.ShowModal() != true) {
                return;
            }
            if (window.MergeIntoSelected && ActiveTab is { } tab) {
                ApplyMatch(tab.Item.Doc, match);
                SaveGpo(tab.Item);
                tab.BuildTree();
            } else {
                var doc = new GpoDocument { Name = window.NewName, Description = $"Imported from {Path.GetFileName(file)}" };
                ApplyMatch(doc, match);
                AddGpo(doc);
            }
        } catch (Exception ex) {
            Dialogs.Error("Import failed", ex.Message);
        }
    }

    private static void ApplyMatch(GpoDocument gpo, MatchResult match) {
        foreach (var s in match.Computer) {
            Upsert(gpo.Computer, s);
        }
        foreach (var s in match.User) {
            Upsert(gpo.User, s);
        }
        gpo.ComputerExtraRegistry.AddRange(match.UnmatchedComputer);
        gpo.UserExtraRegistry.AddRange(match.UnmatchedUser);
    }

    [RelayCommand]
    private void Export() {
        if (ActiveTab?.Item is not { } item) {
            return;
        }
        if (Catalog == null) {
            Dialogs.Error("Export", "The administrative templates are not loaded.");
            return;
        }
        new ExportWindow(item.Doc, Catalog, _settings).ShowModal();
    }

    [RelayCommand]
    private async Task ReportAsync() {
        if (ActiveTab?.Item is not { } item || !await EnsureCatalogForReport()) {
            return;
        }
        try {
            var html = await BuildReport(item.Doc);
            var folder = Path.Combine(Path.GetTempPath(), "ADMX2Reg");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, SafeFileName(item.Name) + ".html");
            await File.WriteAllTextAsync(path, html, new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        } catch (Exception ex) {
            Dialogs.Error("Report failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveReportAsync() {
        if (ActiveTab?.Item is not { } item || !await EnsureCatalogForReport()) {
            return;
        }
        var dialog = new SaveFileDialog {
            Title = "Save report",
            Filter = "HTML report (*.html)|*.html",
            FileName = SafeFileName(item.Name) + ".html",
            InitialDirectory = FolderOrNull(_settings.LastExportFolder) ?? ""
        };
        if (dialog.ShowDialog() != true) {
            return;
        }
        try {
            var html = await BuildReport(item.Doc);
            await File.WriteAllTextAsync(dialog.FileName, html, new UTF8Encoding(true));
            _settings.LastExportFolder = Path.GetDirectoryName(dialog.FileName);
            _settings.Save();
        } catch (Exception ex) {
            Dialogs.Error("Report failed", ex.Message);
        }
    }

    private Task<bool> EnsureCatalogForReport() {
        if (Catalog == null) {
            Dialogs.Error("Report", "The administrative templates are not loaded.");
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    private Task<string> BuildReport(GpoDocument gpo) {
        var catalog = Catalog!;
        return Task.Run(() => HtmlReportWriter.Build(gpo, catalog));
    }

    internal static string SafeFileName(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "GPO" : cleaned;
    }

    internal static string? FolderOrNull(string? folder) => !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;

    // ------------------------------------------------------------------ app commands

    [RelayCommand]
    private Task ReloadAdmxAsync() => LoadCatalogAsync();

    [RelayCommand]
    private async Task ReloadAllAsync() {
        LoadGpos(OpenTabs.Select(t => t.Item.Doc.Id).ToList(), ActiveTab?.Item.Doc.Id);
        await LoadCatalogAsync();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync() {
        var oldPath = _settings.PolicyDefinitionsPath;
        var oldLanguage = _settings.Language;
        var oldFolder = _settings.GpoFolder;
        var window = new SettingsWindow(_settings);
        if (window.ShowModal() != true) {
            return;
        }
        _settings.Save();
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(GpoFolderText));
        if (!string.Equals(oldFolder, _settings.GpoFolder, StringComparison.OrdinalIgnoreCase)) {
            LoadGpos(null, null);
        }
        if (!string.Equals(oldPath, _settings.PolicyDefinitionsPath, StringComparison.OrdinalIgnoreCase) || oldLanguage != _settings.Language) {
            await LoadCatalogAsync();
        }
    }

    [RelayCommand]
    private void ShowWarnings() {
        var lines = new List<string>();
        if (Catalog != null) {
            lines.AddRange(Catalog.Warnings);
        }
        lines.AddRange(_gpoLoadErrors);
        new ListWindow("Load warnings", $"{lines.Count} warning(s) while loading.", lines, false).ShowModal();
    }
}

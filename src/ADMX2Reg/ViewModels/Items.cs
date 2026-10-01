using System.Windows.Media;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace ADMX2Reg.ViewModels;

/// <summary>List entry in the "Group Policy Objects" column.</summary>
public sealed class GpoItem(GpoDocument doc) : ObservableObject {
    public GpoDocument Doc { get; } = doc;
    public string Name => Doc.Name;
    public string? Description => string.IsNullOrWhiteSpace(Doc.Description) ? null : Doc.Description;
    public string ModifiedText => Doc.Modified.LocalDateTime.ToString("g");

    public void Refresh() => OnPropertyChanged(string.Empty);

    // ItemsControl containers use ToString() as the automation name (screen readers, UI Automation).
    public override string ToString() => Name;
}

/// <summary>One policy in the right-hand list.</summary>
public sealed class PolicyRowVm(PolicyDefinition definition, PolicyScope scope, PolicySetting? setting, string categoryPath, string? versionWarning = null) : ObservableObject {
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x5B)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)));
    private static readonly Brush Gray = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    public PolicyDefinition Definition { get; } = definition;
    public PolicyScope Scope { get; } = scope;
    public PolicySetting? Setting { get; private set; } = setting;
    public string CategoryPath { get; } = categoryPath;

    /// <summary>Tooltip for a configured setting that does not match the selected Windows version.</summary>
    public string? VersionWarning => IsConfigured ? versionWarning : null;
    public bool HasVersionWarning => VersionWarning != null;

    public string Name => Definition.DisplayName;
    public PolicyState State => Setting?.State ?? PolicyState.NotConfigured;
    public bool IsConfigured => State != PolicyState.NotConfigured;
    public string StateText => State switch {
        PolicyState.Enabled => "Enabled",
        PolicyState.Disabled => "Disabled",
        _ => "Not configured"
    };
    public string Comment => Setting?.Comment ?? "";
    public SymbolRegular StateSymbol => State switch {
        PolicyState.Enabled => SymbolRegular.CheckmarkCircle24,
        PolicyState.Disabled => SymbolRegular.DismissCircle24,
        _ => SymbolRegular.Circle24
    };
    public Brush StateBrush => State switch {
        PolicyState.Enabled => Green,
        PolicyState.Disabled => Red,
        _ => Gray
    };

    public override string ToString() => Name;

    public void Update(PolicySetting? newSetting) {
        Setting = newSetting;
        OnPropertyChanged(string.Empty);
    }

    private static Brush Freeze(SolidColorBrush brush) {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Entry of the "Windows version" filter; a null <see cref="Version"/> means all versions.</summary>
public sealed record WindowsVersionOption(WindowsVersion? Version, string Text) {
    public override string ToString() => Text;
}

/// <summary>One row of the "Extra Registry Settings" grid.</summary>
public sealed class ExtraRow(RegistryOperation operation) {
    public RegistryOperation Operation { get; } = operation;
    public string Hive => Operation.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
    public string Key => Operation.Key;
    public string ValueName => Operation.Kind is RegistryOperationKind.SetValue or RegistryOperationKind.DeleteValue
        ? (string.IsNullOrEmpty(Operation.ValueName) ? "(Default)" : Operation.ValueName)
        : "";
    public string Type => Operation.Kind == RegistryOperationKind.SetValue ? Operation.Type.ToString() : "";
    public string Data => Operation.Kind switch {
        RegistryOperationKind.SetValue => FormatData(Operation.Data),
        RegistryOperationKind.DeleteValue => "Delete value",
        RegistryOperationKind.DeleteAllValues => "Delete all values",
        RegistryOperationKind.DeleteKey => "Delete key",
        RegistryOperationKind.CreateKey => "Create key",
        _ => ""
    };

    private static string FormatData(object? data) => data switch {
        null => "",
        string s => s,
        string[] lines => string.Join(" | ", lines),
        uint u => $"0x{u:x8} ({u})",
        ulong l => $"0x{l:x16} ({l})",
        byte[] bytes => BitConverter.ToString(bytes).Replace('-', ','),
        _ => data.ToString() ?? ""
    };
}

public enum NodeKind {
    Scope,
    AdminTemplates,
    Category,
    Extra
}

/// <summary>Node of the policy tree. Category children are built lazily on first expand.</summary>
public sealed partial class TreeNode : ObservableObject {
    private readonly GpoTabViewModel _owner;
    private Func<IEnumerable<TreeNode>>? _loader;

    public TreeNode(GpoTabViewModel owner, NodeKind kind, PolicyScope scope, string title, SymbolRegular icon, PolicyCategory? category = null) {
        _owner = owner;
        Kind = kind;
        Scope = scope;
        Title = title;
        Icon = icon;
        Category = category;
    }

    public NodeKind Kind { get; }
    public PolicyScope Scope { get; }
    public string Title { get; }
    public SymbolRegular Icon { get; }
    public PolicyCategory? Category { get; }
    public TreeNode? Parent { get; private set; }
    public bool IsPlaceholder { get; private init; }
    public System.Collections.ObjectModel.ObservableCollection<TreeNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigured))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    private int _configured;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True when the "Configured only" filter hides this node.</summary>
    [ObservableProperty]
    private bool _isHidden;

    public bool HasConfigured => Configured > 0;
    public System.Windows.FontWeight TitleWeight => Configured > 0 ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;

    /// <summary>Registers lazy children: a placeholder is shown until the node is expanded.</summary>
    public void SetLazyChildren(Func<IEnumerable<TreeNode>> loader) {
        _loader = loader;
        Children.Add(new TreeNode(_owner, NodeKind.Category, Scope, "", SymbolRegular.Circle24) { IsPlaceholder = true });
    }

    public bool IsLoaded => _loader == null;

    public void AddChild(TreeNode child) {
        child.Parent = this;
        Children.Add(child);
    }

    public override string ToString() => Title;

    public void EnsureLoaded() {
        if (_loader == null) {
            return;
        }
        var loader = _loader;
        _loader = null;
        Children.Clear();
        foreach (var child in loader()) {
            AddChild(child);
        }
        _owner.RefreshCounts();
    }

    partial void OnIsExpandedChanged(bool value) {
        if (value) {
            EnsureLoaded();
        }
    }

    partial void OnIsSelectedChanged(bool value) {
        if (value) {
            _owner.OnNodeSelected(this);
        }
    }
}

/// <summary>One part of the breadcrumb above the policy list.</summary>
public sealed record Crumb(string Text, bool IsLast) {
    public bool HasSeparator => !IsLast;
}

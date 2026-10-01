namespace ADMX2Reg.Core.Admx;

public enum PolicyClass {
    Machine,
    User,
    Both
}

/// <summary>Everything loaded from one PolicyDefinitions folder in one language.</summary>
public sealed class AdmxCatalog {
    public required string SourcePath { get; init; }
    public required string Language { get; init; }

    /// <summary>Top level categories (no parent, or parent not found). Sorted by display name.</summary>
    public List<PolicyCategory> RootCategories { get; } = [];

    /// <summary>All categories keyed by <see cref="PolicyCategory.Id"/>.</summary>
    public Dictionary<string, PolicyCategory> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All policies keyed by <see cref="PolicyDefinition.Id"/>.</summary>
    public Dictionary<string, PolicyDefinition> Policies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Non-fatal problems found while loading (missing ADML, bad references, etc.).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Windows versions from the "MicrosoftWindows" product, newest first.</summary>
    public List<WindowsVersion> WindowsVersions { get; } = [];
}

/// <summary>A Windows major version from WindowsProducts.admx. <see cref="Index"/> is its versionIndex.</summary>
public sealed record WindowsVersion(int Index, string Name, string DisplayName);

/// <summary>Inclusive range of Windows version indexes.</summary>
public sealed record VersionRange(int Min, int Max);

public sealed class PolicyCategory {
    /// <summary>"{targetNamespace}:{name}", e.g. "Microsoft.Policies.Windows:WindowsComponents".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? ExplainText { get; init; }
    public string? ParentId { get; init; }
    public PolicyCategory? Parent { get; set; }

    /// <summary>Sorted by display name.</summary>
    public List<PolicyCategory> Children { get; } = [];

    /// <summary>Policies directly in this category, sorted by display name.</summary>
    public List<PolicyDefinition> Policies { get; } = [];
}

public sealed class PolicyDefinition {
    /// <summary>"{targetNamespace}:{name}". Stable identifier used in saved GPOs.</summary>
    public string Id => $"{Namespace}:{Name}";
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string ExplainText { get; init; } = "";
    public string SupportedOn { get; init; } = "";
    public string? CategoryId { get; init; }
    public PolicyCategory? Category { get; set; }
    public required string SourceFile { get; init; }
    public PolicyClass Class { get; init; }

    public required string Key { get; init; }
    public string? ValueName { get; init; }

    public PolicyValue? EnabledValue { get; init; }
    public PolicyValue? DisabledValue { get; init; }
    public PolicyValueList? EnabledList { get; init; }
    public PolicyValueList? DisabledList { get; init; }

    public List<PolicyElement> Elements { get; } = [];
    public PolicyPresentation? Presentation { get; init; }

    /// <summary>Windows version ranges from supportedOn; null when supportedOn says nothing about Windows versions.</summary>
    public List<VersionRange>? WindowsSupport { get; set; }

    /// <summary>True/false when supportedOn names Windows versions, null when it does not.</summary>
    public bool? SupportsWindows(int index) =>
        WindowsSupport == null ? null : WindowsSupport.Any(r => index >= r.Min && index <= r.Max);

    public bool AppliesTo(Gpo.PolicyScope scope) =>
        Class == PolicyClass.Both
        || (Class == PolicyClass.Machine && scope == Gpo.PolicyScope.Computer)
        || (Class == PolicyClass.User && scope == Gpo.PolicyScope.User);
}

public enum PolicyValueKind {
    /// <summary>REG_DWORD (or REG_SZ when the owning element has storeAsText).</summary>
    Decimal,
    /// <summary>REG_QWORD.</summary>
    LongDecimal,
    /// <summary>REG_SZ.</summary>
    String,
    /// <summary>The value is deleted.</summary>
    Delete
}

/// <summary>An ADMX &lt;value&gt; node: &lt;decimal value=""/&gt;, &lt;longDecimal/&gt;, &lt;string&gt;, or &lt;delete/&gt;.</summary>
public sealed record PolicyValue(PolicyValueKind Kind, ulong Number = 0, string? Text = null);

/// <summary>An ADMX enabledList / disabledList / trueList / falseList / valueList.</summary>
public sealed class PolicyValueList {
    /// <summary>The list's defaultKey attribute; items without their own key use it.</summary>
    public string? DefaultKey { get; init; }
    public List<PolicyListItem> Items { get; } = [];
}

public sealed record PolicyListItem(string? Key, string ValueName, PolicyValue Value);

// ---------------------------------------------------------------- elements

public abstract class PolicyElement {
    public required string Id { get; init; }
    /// <summary>Overrides the policy key when set.</summary>
    public string? Key { get; init; }
    public string? ValueName { get; init; }
}

public sealed class BooleanElement : PolicyElement {
    public PolicyValue? TrueValue { get; init; }
    public PolicyValue? FalseValue { get; init; }
    public PolicyValueList? TrueList { get; init; }
    public PolicyValueList? FalseList { get; init; }
}

public sealed class DecimalElement : PolicyElement {
    public bool Required { get; init; }
    public uint MinValue { get; init; }
    public uint MaxValue { get; init; } = 9999;
    public bool StoreAsText { get; init; }
    public bool Soft { get; init; }
}

public sealed class LongDecimalElement : PolicyElement {
    public bool Required { get; init; }
    public ulong MinValue { get; init; }
    public ulong MaxValue { get; init; } = 9999;
    public bool StoreAsText { get; init; }
    public bool Soft { get; init; }
}

public sealed class TextElement : PolicyElement {
    public bool Required { get; init; }
    public int MaxLength { get; init; } = 1023;
    public bool Expandable { get; init; }
    public bool Soft { get; init; }
}

public sealed class MultiTextElement : PolicyElement {
    public bool Required { get; init; }
    public int MaxLength { get; init; } = 1023;
    public int MaxStrings { get; init; }
    public bool Soft { get; init; }
}

public sealed class EnumElement : PolicyElement {
    public bool Required { get; init; }
    public List<EnumItem> Items { get; } = [];
}

public sealed class EnumItem {
    public required string DisplayName { get; init; }
    public required PolicyValue Value { get; init; }
    public PolicyValueList? ValueList { get; init; }
}

public sealed class ListElement : PolicyElement {
    public bool Additive { get; init; }
    public bool ExplicitValue { get; init; }
    public bool Expandable { get; init; }
    public string? ValuePrefix { get; init; }
}

// ---------------------------------------------------------------- presentation

public sealed class PolicyPresentation {
    public required string Id { get; init; }
    public List<PresentationControl> Controls { get; } = [];
}

public abstract class PresentationControl {
    /// <summary>The element id this control edits; null for plain text labels.</summary>
    public string? RefId { get; init; }
    public string Label { get; init; } = "";
}

public sealed class TextLabelControl : PresentationControl;

public sealed class DecimalTextBoxControl : PresentationControl {
    public ulong DefaultValue { get; init; } = 1;
    public bool Spin { get; init; } = true;
    public ulong SpinStep { get; init; } = 1;
}

public sealed class LongDecimalTextBoxControl : PresentationControl {
    public ulong DefaultValue { get; init; } = 1;
    public bool Spin { get; init; } = true;
    public ulong SpinStep { get; init; } = 1;
}

public sealed class TextBoxControl : PresentationControl {
    public string? DefaultValue { get; init; }
}

public sealed class MultiTextBoxControl : PresentationControl {
    public int DefaultHeight { get; init; } = 3;
}

public sealed class CheckBoxControl : PresentationControl {
    public bool DefaultChecked { get; init; }
}

public sealed class ComboBoxControl : PresentationControl {
    public string? DefaultValue { get; init; }
    public List<string> Suggestions { get; } = [];
    public bool NoSort { get; init; }
}

public sealed class DropdownListControl : PresentationControl {
    public int? DefaultItem { get; init; }
    public bool NoSort { get; init; }
}

public sealed class ListBoxControl : PresentationControl;

using System.Text.Json.Serialization;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core.Gpo;

public enum PolicyState {
    NotConfigured,
    Enabled,
    Disabled
}

public enum PolicyScope {
    Computer,
    User
}

/// <summary>A named set of policy settings, saved as one JSON file. Never applied, only exported.</summary>
public sealed class GpoDocument {
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Group Policy Object";
    public string? Description { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;

    /// <summary>Computer Configuration settings (HKLM). Only Enabled/Disabled entries are stored.</summary>
    public List<PolicySetting> Computer { get; set; } = [];

    /// <summary>User Configuration settings (HKCU). Only Enabled/Disabled entries are stored.</summary>
    public List<PolicySetting> User { get; set; } = [];

    /// <summary>Raw registry operations that did not map to any ADMX policy (from imports). Exported as-is.</summary>
    public List<RegistryOperation> ComputerExtraRegistry { get; set; } = [];
    public List<RegistryOperation> UserExtraRegistry { get; set; } = [];

    /// <summary>Full path of the JSON file this document was loaded from or saved to.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    public List<PolicySetting> Settings(PolicyScope scope) => scope == PolicyScope.Computer ? Computer : User;
    public List<RegistryOperation> ExtraRegistry(PolicyScope scope) => scope == PolicyScope.Computer ? ComputerExtraRegistry : UserExtraRegistry;
}

public sealed class PolicySetting {
    /// <summary><see cref="Admx.PolicyDefinition.Id"/>.</summary>
    public required string PolicyId { get; set; }
    public PolicyState State { get; set; }
    public string? Comment { get; set; }

    /// <summary>Element values keyed by element id. Only meaningful when State is Enabled.</summary>
    public Dictionary<string, PolicyElementValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Value of one policy element. Exactly one member is used, depending on the element type:
/// Boolean -> Boolean, Decimal/LongDecimal -> Number, Text -> Text, MultiText -> Lines,
/// Enum -> EnumIndex (index into EnumElement.Items), List -> Entries.
/// </summary>
public sealed class PolicyElementValue {
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Boolean { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ulong? Number { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Lines { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? EnumIndex { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ListEntry>? Entries { get; set; }
}

/// <summary>One row of a list element. Name is only used when the list has explicitValue="true".</summary>
public sealed class ListEntry {
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    public override string ToString() => string.IsNullOrEmpty(Name) ? Value : $"{Name} = {Value}";
}

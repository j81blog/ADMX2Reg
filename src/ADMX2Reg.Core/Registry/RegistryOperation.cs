namespace ADMX2Reg.Core.Registry;

public enum RegistryHive {
    LocalMachine,
    CurrentUser
}

public enum RegistryOperationKind {
    /// <summary>Create or overwrite a value.</summary>
    SetValue,
    /// <summary>Delete a single value (Registry.pol: **del.ValueName).</summary>
    DeleteValue,
    /// <summary>Delete all values in a key but keep the key and its subkeys (Registry.pol: **delvals.).</summary>
    DeleteAllValues,
    /// <summary>Delete the key and everything below it.</summary>
    DeleteKey,
    /// <summary>Ensure the key exists without setting a value.</summary>
    CreateKey
}

public enum RegValueType {
    None,
    String,
    ExpandString,
    DWord,
    QWord,
    MultiString,
    Binary
}

/// <summary>
/// One atomic registry change. Every exporter (.reg, .ps1, Registry.pol) consumes a list of these,
/// and every importer produces them.
/// </summary>
/// <remarks>
/// Data carries: string for String/ExpandString, string[] for MultiString, uint for DWord,
/// ulong for QWord, byte[] for Binary, null for None or for non-SetValue kinds.
/// Key is relative to the hive, without leading or trailing backslash (e.g. "Software\Policies\Microsoft\Edge").
/// </remarks>
public sealed record RegistryOperation {
    public required RegistryHive Hive { get; init; }
    public required RegistryOperationKind Kind { get; init; }
    public required string Key { get; init; }
    public string? ValueName { get; init; }
    public RegValueType Type { get; init; } = RegValueType.None;
    public object? Data { get; init; }

    public static RegistryOperation Set(RegistryHive hive, string key, string valueName, RegValueType type, object? data) =>
        new() { Hive = hive, Kind = RegistryOperationKind.SetValue, Key = key, ValueName = valueName, Type = type, Data = data };

    public static RegistryOperation DeleteValue(RegistryHive hive, string key, string valueName) =>
        new() { Hive = hive, Kind = RegistryOperationKind.DeleteValue, Key = key, ValueName = valueName };

    public static RegistryOperation DeleteAllValues(RegistryHive hive, string key) =>
        new() { Hive = hive, Kind = RegistryOperationKind.DeleteAllValues, Key = key };

    public static RegistryOperation DeleteKey(RegistryHive hive, string key) =>
        new() { Hive = hive, Kind = RegistryOperationKind.DeleteKey, Key = key };
}

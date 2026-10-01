using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADMX2Reg.Core;

namespace ADMX2Reg.Services;

public enum ThemeChoice {
    System,
    Light,
    Dark
}

/// <summary>User settings, stored as settings.json next to the exe (or in %LOCALAPPDATA%\ADMX2Reg when that folder is read-only).</summary>
public sealed class AppSettings {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// PolicyDefinitions folder as stored: relative to the exe folder (for templates shipped with the app) or a full path.
    /// Use <see cref="PolicyDefinitionsFullPath"/> to read the files.
    /// </summary>
    public string PolicyDefinitionsPath { get; set; } = DefaultPolicyDefinitionsPath();

    /// <summary>Full path of <see cref="PolicyDefinitionsPath"/>.</summary>
    [JsonIgnore]
    public string PolicyDefinitionsFullPath => ResolvePath(PolicyDefinitionsPath);
    public string? Language { get; set; }
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>
    /// GPO folder as stored: relative to the exe folder (portable default ".\GPOs") or a full path.
    /// Use <see cref="GpoFolderPath"/> to read or write files.
    /// </summary>
    public string GpoFolder { get; set; } = DefaultGpoFolder();

    /// <summary>Full path of <see cref="GpoFolder"/>.</summary>
    [JsonIgnore]
    public string GpoFolderPath => ResolvePath(GpoFolder);
    public string? LastExportFolder { get; set; }
    public string? LastImportFolder { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Name of the Windows version used to filter policies; null means all versions.</summary>
    public string? WindowsVersionFilter { get; set; }

    /// <summary>With a version filter: also show policies that are not tied to a Windows version.</summary>
    public bool IncludeUnversioned { get; set; } = true;

    /// <summary>Workbench layout: side panel visibility and width, GPO section height (null = fit content), bottom panel height and state.</summary>
    public bool SidePanelVisible { get; set; } = true;
    public bool RailExpanded { get; set; }
    public double? SidePanelWidth { get; set; }
    public double? GpoListHeight { get; set; }
    public double? BottomPanelHeight { get; set; }
    public bool BottomPanelCollapsed { get; set; }

    /// <summary>Ids of the GPOs that were open in tabs (null = never saved) and the active one.</summary>
    public List<Guid>? OpenGpoIds { get; set; }
    public Guid? ActiveGpoId { get; set; }

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    private static string BaseFolder => AppContext.BaseDirectory;

    private static string LocalAppFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADMX2Reg");

    private const string PortableGpoFolder = @".\GPOs";
    private const string PortablePolicyDefinitions = @".\PolicyDefinitions";

    // Templates shipped next to the exe win over the local store on first start.
    private static string DefaultPolicyDefinitionsPath() {
        var bundled = Path.Combine(BaseFolder, "PolicyDefinitions");
        return Directory.Exists(bundled) && Directory.EnumerateFiles(bundled, "*.admx").Any()
            ? PortablePolicyDefinitions
            : AdmxLoader.DefaultPolicyDefinitionsPath;
    }

    private static string DefaultGpoFolder() =>
        IsWritable(BaseFolder) ? PortableGpoFolder : Path.Combine(LocalAppFolder, "GPOs");

    /// <summary>A relative path is relative to the exe folder; a full path is used as is.</summary>
    public static string ResolvePath(string path) =>
        Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(BaseFolder, path));

    /// <summary>Path to store: relative (".\...") when it lies inside the exe folder, so the folder can move with the exe.</summary>
    public static string ToStoredPath(string path) {
        if (!Path.IsPathFullyQualified(path)) {
            return path;
        }
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(BaseFolder).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) {
            return ".";
        }
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "." + Path.DirectorySeparatorChar + full[(root.Length + 1)..]
            : path;
    }

    private static bool IsWritable(string folder) {
        try {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        } catch {
            return false;
        }
    }

    public static AppSettings Load() {
        var portable = Path.Combine(BaseFolder, "settings.json");
        var local = Path.Combine(LocalAppFolder, "settings.json");
        string path;
        if (File.Exists(portable)) {
            path = portable;
        } else if (File.Exists(local)) {
            path = local;
        } else {
            path = IsWritable(BaseFolder) ? portable : local;
        }

        AppSettings? settings = null;
        try {
            if (File.Exists(path)) {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            }
        } catch {
            // Corrupt settings: start with defaults.
        }

        settings ??= new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.PolicyDefinitionsPath)) {
            settings.PolicyDefinitionsPath = DefaultPolicyDefinitionsPath();
        } else {
            settings.PolicyDefinitionsPath = ToStoredPath(settings.PolicyDefinitionsPath);
        }
        if (string.IsNullOrWhiteSpace(settings.GpoFolder)) {
            settings.GpoFolder = DefaultGpoFolder();
        } else {
            // Older settings stored the portable folder as a full path; keep it relative from now on.
            settings.GpoFolder = ToStoredPath(settings.GpoFolder);
        }
        settings.FilePath = path;
        return settings;
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        } catch {
            // Saving settings must never crash the app.
        }
    }
}

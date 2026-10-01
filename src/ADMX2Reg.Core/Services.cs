// Public API surface of ADMX2Reg.Core. Signatures here are the contract between the core and the UI;
// implementations live in the matching folders. Do not change a signature without updating all callers.
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Core.Registry;

namespace ADMX2Reg.Core;

public static partial class AdmxLoader {
    /// <summary>Default local store.</summary>
    public const string DefaultPolicyDefinitionsPath = @"C:\Windows\PolicyDefinitions";

    /// <summary>
    /// Loads all *.admx in <paramref name="policyDefinitionsPath"/> plus the matching ADML from the
    /// language subfolder. Language resolution: <paramref name="language"/> if given, else current UI culture,
    /// falling back per file to en-US, then to any available language. Never throws for a single bad file;
    /// it adds a warning instead.
    /// </summary>
    public static partial Task<AdmxCatalog> LoadAsync(string policyDefinitionsPath, string? language = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Language folder names (e.g. "en-US", "nl-NL") present in the PolicyDefinitions folder.</summary>
    public static partial IReadOnlyList<string> GetAvailableLanguages(string policyDefinitionsPath);

    /// <summary>
    /// The domain central store path (\\domain\SYSVOL\domain\Policies\PolicyDefinitions) when the machine is
    /// domain joined and the folder exists, else null.
    /// </summary>
    public static partial string? TryGetCentralStorePath();
}

public static partial class PolicyEvaluator {
    /// <summary>
    /// Translates one policy setting into registry operations, mirroring the Group Policy engine:
    /// Enabled writes enabledValue/enabledList/element values; Disabled writes disabledValue/disabledList and
    /// deletes element values; NotConfigured returns nothing.
    /// </summary>
    public static partial IReadOnlyList<RegistryOperation> Evaluate(PolicyDefinition definition, PolicySetting setting, PolicyScope scope);
}

public sealed record CompileResult(IReadOnlyList<RegistryOperation> Operations, IReadOnlyList<string> Warnings);

public static partial class GpoCompiler {
    /// <summary>
    /// All registry operations for one scope of a GPO: each configured policy (in category/display order)
    /// followed by the extra registry operations. Settings whose policy is missing from the catalog produce a warning.
    /// </summary>
    public static partial CompileResult Compile(GpoDocument gpo, AdmxCatalog catalog, PolicyScope scope);
}

public sealed partial class GpoStore {
    public GpoStore(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>Loads every *.json GPO in the folder. Corrupt files are skipped and reported in <paramref name="errors"/>.</summary>
    public partial List<GpoDocument> LoadAll(out List<string> errors);

    /// <summary>Saves to gpo.FilePath, or to "{Folder}\{Id}.json" when FilePath is null. Updates Modified and FilePath.</summary>
    public partial void Save(GpoDocument gpo);

    public partial void Delete(GpoDocument gpo);

    /// <summary>Deep copy with a new Id, " (Copy)" suffix on the name, and no FilePath.</summary>
    public static partial GpoDocument Duplicate(GpoDocument gpo);

    /// <summary>Serializer used for GPO files (also round-trips RegistryOperation.Data with its type).</summary>
    public static partial string Serialize(GpoDocument gpo);
    public static partial GpoDocument Deserialize(string json);
}

// ---------------------------------------------------------------- export

public enum PowerShellStyle {
    /// <summary>Self-contained, idempotent, admin check for HKLM, logging, -WhatIf support.</summary>
    Standalone,
    /// <summary>Flat list of New-Item / Set-ItemProperty / Remove-ItemProperty commands.</summary>
    Minimal
}

public static partial class RegFileWriter {
    /// <summary>Writes a "Windows Registry Editor Version 5.00" file. <paramref name="headerComment"/> lines are written as ; comments.</summary>
    public static partial void Write(IEnumerable<RegistryOperation> operations, TextWriter writer, string? headerComment = null);
}

public static partial class PowerShellWriter {
    /// <summary>Writes a Windows PowerShell 5.1 compatible script that applies the operations.</summary>
    public static partial void Write(IEnumerable<RegistryOperation> operations, TextWriter writer, PowerShellStyle style, string? headerComment = null);
}

public static partial class PolFileWriter {
    /// <summary>Writes a PReg (Registry.pol) file. The hive of each operation is ignored; callers pass one scope per file.</summary>
    public static partial void Write(IEnumerable<RegistryOperation> operations, Stream stream);
}

// ---------------------------------------------------------------- import

public static partial class RegFileReader {
    /// <summary>Parses a .reg file (version 5.00 or REGEDIT4). Only HKLM and HKCU keys are returned; others are reported as warnings.</summary>
    public static partial IReadOnlyList<RegistryOperation> Read(TextReader reader, List<string> warnings);
}

public static partial class PolFileReader {
    /// <summary>Parses a PReg (Registry.pol) file. All operations get <paramref name="hive"/>.</summary>
    public static partial IReadOnlyList<RegistryOperation> Read(Stream stream, RegistryHive hive);
}

public sealed record MatchResult(
    List<PolicySetting> Computer,
    List<PolicySetting> User,
    List<RegistryOperation> UnmatchedComputer,
    List<RegistryOperation> UnmatchedUser);

public static partial class PolicyMatcher {
    /// <summary>
    /// Maps raw registry operations back onto ADMX policies (state plus element values). Operations that
    /// cannot be explained by a policy are returned as unmatched so nothing gets lost on import.
    /// </summary>
    public static partial MatchResult Match(IEnumerable<RegistryOperation> operations, AdmxCatalog catalog);
}

// ---------------------------------------------------------------- report

public static partial class HtmlReportWriter {
    /// <summary>GPMC-style self-contained HTML settings report for one GPO (both scopes, extra registry included).</summary>
    public static partial string Build(GpoDocument gpo, AdmxCatalog catalog);
}

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ADMX2Reg.Core.Admx;

namespace ADMX2Reg.Core;

public static partial class AdmxLoader {
    private static readonly Regex StringRef = new(@"^\$\((string|presentation)\.(.+)\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CultureLike = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static partial async Task<AdmxCatalog> LoadAsync(string policyDefinitionsPath, string? language,
        IProgress<string>? progress, CancellationToken cancellationToken) {
        var requested = string.IsNullOrWhiteSpace(language) ? CultureInfo.CurrentUICulture.Name : language;
        if (string.IsNullOrEmpty(requested)) {
            requested = "en-US";
        }
        var catalog = new AdmxCatalog { SourcePath = policyDefinitionsPath, Language = requested };
        if (!Directory.Exists(policyDefinitionsPath)) {
            catalog.Warnings.Add($"Folder '{policyDefinitionsPath}' does not exist.");
            return catalog;
        }

        return await Task.Run(() => Load(policyDefinitionsPath, requested, catalog, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static partial IReadOnlyList<string> GetAvailableLanguages(string policyDefinitionsPath) {
        var result = new List<string>();
        try {
            foreach (var dir in Directory.EnumerateDirectories(policyDefinitionsPath)) {
                var name = Path.GetFileName(dir);
                if (!IsCultureName(name)) {
                    continue;
                }
                if (Directory.EnumerateFiles(dir, "*.adml").Any()) {
                    result.Add(name);
                }
            }
        } catch (Exception) {
            // Missing or unreadable folder: no languages.
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public static partial string? TryGetCentralStorePath() {
        try {
            var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
            if (string.IsNullOrWhiteSpace(domain)) {
                domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            }
            if (string.IsNullOrWhiteSpace(domain)) {
                return null;
            }
            var path = $@"\\{domain}\SYSVOL\{domain}\Policies\PolicyDefinitions";
            var check = Task.Run(() => {
                try { return Directory.Exists(path); } catch (Exception) { return false; }
            });
            return check.Wait(TimeSpan.FromSeconds(5)) && check.Result ? path : null;
        } catch (Exception) {
            return null;
        }
    }

    private static bool IsCultureName(string name) {
        if (!CultureLike.IsMatch(name)) {
            return false;
        }
        try {
            CultureInfo.GetCultureInfo(name);
            return true;
        } catch (CultureNotFoundException) {
            return false;
        }
    }

    // ------------------------------------------------------------ loading

    private sealed class ParsedFile {
        public required string FileName { get; init; }
        public required XDocument Admx { get; init; }
        public required string Namespace { get; init; }
        public required Dictionary<string, string> Prefixes { get; init; }
        public Dictionary<string, string> Strings { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>String table of the en-US ADML (same as <see cref="Strings"/> when that is the loaded language).</summary>
        public Dictionary<string, string> EnStrings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public XElement? AdmlRoot { get; set; }
        public List<string> Warnings { get; } = [];
        public int Unresolved { get; set; }
        public string? FirstUnresolved { get; set; }
        public List<PolicyCategory> Categories { get; } = [];
        public List<PolicyDefinition> Policies { get; } = [];
        public Dictionary<string, PolicyPresentation> Presentations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Res(string? raw) {
            if (string.IsNullOrEmpty(raw)) {
                return "";
            }
            var m = StringRef.Match(raw);
            if (!m.Success || m.Groups[1].Value != "string") {
                return raw;
            }
            var id = m.Groups[2].Value;
            if (Strings.TryGetValue(id, out var value)) {
                return value;
            }
            if (AdmlRoot != null) {
                Unresolved++;
                FirstUnresolved ??= id;
            }
            return id;
        }

        public string ResolveRef(string reference) {
            var idx = reference.IndexOf(':');
            if (idx < 0) {
                return $"{Namespace}:{reference}";
            }
            var prefix = reference[..idx];
            var name = reference[(idx + 1)..];
            return Prefixes.TryGetValue(prefix, out var ns) ? $"{ns}:{name}" : reference;
        }
    }

    private static AdmxCatalog Load(string path, string requested, AdmxCatalog catalog, IProgress<string>? progress, CancellationToken ct) {
        var files = Directory.GetFiles(path, "*.admx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var languageFolders = Directory.GetDirectories(path).Select(Path.GetFileName).Where(n => n != null && IsCultureName(n)).Select(n => n!).ToList();
        var options = new ParallelOptions { CancellationToken = ct };
        progress?.Report($"Loading {files.Length} ADMX files");

        // Phase 1: read ADMX and ADML, collect supportedOn definitions.
        var parsed = new ParsedFile?[files.Length];
        var failures = new string?[files.Length];
        var done = 0;
        Parallel.For(0, files.Length, options, i => {
            try {
                parsed[i] = ReadFile(files[i], path, requested, languageFolders);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                failures[i] = $"{Path.GetFileName(files[i])}: could not be loaded ({ex.Message})";
            }
            var n = Interlocked.Increment(ref done);
            if (n % 20 == 0 || n == files.Length) {
                progress?.Report($"Read {n} of {files.Length} files");
            }
        });
        foreach (var f in failures) {
            if (f != null) {
                catalog.Warnings.Add(f);
            }
        }
        var ok = parsed.Where(p => p != null).Select(p => p!).ToList();

        var supportedOn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ok) {
            CollectSupportedOn(file, supportedOn);
        }
        var windows = new WindowsInfo(ok, catalog);

        // Phase 2: policies, categories and presentations.
        progress?.Report("Parsing policies");
        Parallel.ForEach(ok, options, file => {
            try {
                ParseDefinitions(file, supportedOn, windows);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                file.Warnings.Add($"{file.FileName}: parse error ({ex.Message})");
            }
        });

        // Phase 3: merge sequentially.
        progress?.Report("Building category tree");
        foreach (var file in ok) {
            catalog.Warnings.AddRange(file.Warnings);
            if (file.Unresolved > 0) {
                catalog.Warnings.Add($"{file.FileName}: {file.Unresolved} unresolved string reference(s), e.g. '{file.FirstUnresolved}'.");
            }
            foreach (var c in file.Categories) {
                if (!catalog.Categories.TryAdd(c.Id, c)) {
                    catalog.Warnings.Add($"{file.FileName}: duplicate category '{c.Id}' ignored.");
                }
            }
            foreach (var p in file.Policies) {
                if (!catalog.Policies.TryAdd(p.Id, p)) {
                    catalog.Warnings.Add($"{file.FileName}: duplicate policy '{p.Id}' ignored.");
                }
            }
        }

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        foreach (var c in catalog.Categories.Values) {
            if (c.ParentId != null && catalog.Categories.TryGetValue(c.ParentId, out var parent) && !IsAncestor(c, parent)) {
                c.Parent = parent;
                parent.Children.Add(c);
            } else {
                if (c.ParentId != null) {
                    catalog.Warnings.Add($"Category '{c.Id}': parent '{c.ParentId}' not found or circular, shown at top level.");
                }
                catalog.RootCategories.Add(c);
            }
        }
        foreach (var c in catalog.Categories.Values) {
            c.Children.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));
        }
        catalog.RootCategories.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));

        var missingCategory = 0;
        foreach (var p in catalog.Policies.Values) {
            if (p.CategoryId != null && catalog.Categories.TryGetValue(p.CategoryId, out var cat)) {
                p.Category = cat;
                cat.Policies.Add(p);
            } else {
                missingCategory++;
            }
        }
        if (missingCategory > 0) {
            catalog.Warnings.Add(missingCategory == 1
                ? "1 policy has no (or an unknown) parent category; it is listed directly under Administrative Templates."
                : $"{missingCategory} policies have no (or an unknown) parent category; they are listed directly under Administrative Templates.");
        }
        foreach (var c in catalog.Categories.Values) {
            c.Policies.Sort((a, b) => comparer.Compare(a.DisplayName, b.DisplayName));
        }

        progress?.Report($"Loaded {catalog.Policies.Count} policies");
        return catalog;
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="category"/> or one of its descendants (would create a cycle).</summary>
    private static bool IsAncestor(PolicyCategory category, PolicyCategory candidate) {
        for (var c = candidate; c != null; c = c.Parent) {
            if (ReferenceEquals(c, category)) {
                return true;
            }
        }
        return false;
    }

    private static ParsedFile ReadFile(string admxPath, string root, string requested, List<string> languageFolders) {
        var fileName = Path.GetFileName(admxPath);
        var doc = XDocument.Load(admxPath);
        var rootEl = doc.Root ?? throw new InvalidDataException("no root element");
        var nsEl = Kids(rootEl, "policyNamespaces").FirstOrDefault() ?? throw new InvalidDataException("no policyNamespaces");
        var target = Kids(nsEl, "target").FirstOrDefault();
        var ns = Attr(target, "namespace") ?? throw new InvalidDataException("no target namespace");
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetPrefix = Attr(target, "prefix");
        if (targetPrefix != null) {
            prefixes[targetPrefix] = ns;
        }
        foreach (var u in Kids(nsEl, "using")) {
            var prefix = Attr(u, "prefix");
            var uns = Attr(u, "namespace");
            if (prefix != null && uns != null) {
                prefixes[prefix] = uns;
            }
        }

        var file = new ParsedFile { FileName = fileName, Admx = doc, Namespace = ns, Prefixes = prefixes };
        var admlPath = FindAdml(root, Path.GetFileNameWithoutExtension(admxPath) + ".adml", requested, languageFolders);
        if (admlPath == null) {
            file.Warnings.Add($"{fileName}: no ADML language file found, using raw names.");
            return file;
        }
        try {
            var adml = XDocument.Load(admlPath).Root;
            file.AdmlRoot = adml;
            ReadStrings(adml!, file.Strings);
            file.EnStrings = file.Strings;
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(admlPath)), "en-US", StringComparison.OrdinalIgnoreCase)
                && Kids(rootEl, "supportedOn").Any()) {
                var enPath = Path.Combine(root, "en-US", Path.GetFileNameWithoutExtension(admxPath) + ".adml");
                if (File.Exists(enPath)) {
                    try {
                        file.EnStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        ReadStrings(XDocument.Load(enPath).Root!, file.EnStrings);
                    } catch (Exception) {
                        file.EnStrings = file.Strings;
                    }
                }
            }
        } catch (Exception ex) {
            file.AdmlRoot = null;
            file.Warnings.Add($"{fileName}: ADML could not be read ({ex.Message}), using raw names.");
        }
        return file;
    }

    private static void ReadStrings(XElement adml, Dictionary<string, string> target) {
        foreach (var s in Kids(adml, "resources").SelectMany(r => Kids(r, "stringTable")).SelectMany(t => Kids(t, "string"))) {
            var id = Attr(s, "id");
            if (id != null) {
                target[id] = s.Value;
            }
        }
    }

    // Order: requested language, en-US, next to the ADMX (vendors often ship flat folders, nearly always English),
    // then any other language folder.
    private static string? FindAdml(string root, string admlName, string requested, List<string> languageFolders) {
        foreach (var lang in new[] { requested, "en-US" }) {
            var p = Path.Combine(root, lang, admlName);
            if (File.Exists(p)) {
                return p;
            }
        }
        var flat = Path.Combine(root, admlName);
        if (File.Exists(flat)) {
            return flat;
        }
        foreach (var lang in languageFolders) {
            var p = Path.Combine(root, lang, admlName);
            if (File.Exists(p)) {
                return p;
            }
        }
        return null;
    }

    private static void CollectSupportedOn(ParsedFile file, Dictionary<string, string> target) {
        var so = Kids(file.Admx.Root!, "supportedOn").FirstOrDefault();
        if (so == null) {
            return;
        }
        foreach (var def in Kids(so, "definitions").SelectMany(d => Kids(d, "definition"))) {
            Add(def);
        }
        foreach (var product in Kids(so, "products").SelectMany(p => Kids(p, "product"))) {
            Add(product);
            foreach (var major in Kids(product, "majorVersion")) {
                Add(major);
                foreach (var minor in Kids(major, "minorVersion")) {
                    Add(minor);
                }
            }
        }

        void Add(XElement e) {
            var name = Attr(e, "name");
            if (name != null) {
                target.TryAdd($"{file.Namespace}:{name}", file.Res(Attr(e, "displayName")));
            }
        }
    }

    // ------------------------------------------------------------ windows versions

    /// <summary>Windows product versions and the Windows ranges of every supportedOn definition.</summary>
    private sealed class WindowsInfo {
        private readonly Dictionary<string, int> _versionIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (ParsedFile File, XElement Element)> _definitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<VersionRange>?> _cache = new(StringComparer.OrdinalIgnoreCase);
        private string? _productId;

        public WindowsInfo(List<ParsedFile> files, AdmxCatalog catalog) {
            foreach (var file in files) {
                var so = Kids(file.Admx.Root!, "supportedOn").FirstOrDefault();
                if (so == null) {
                    continue;
                }
                foreach (var def in Kids(so, "definitions").SelectMany(d => Kids(d, "definition"))) {
                    var name = Attr(def, "name");
                    if (name != null) {
                        _definitions.TryAdd($"{file.Namespace}:{name}", (file, def));
                    }
                }
                foreach (var product in Kids(so, "products").SelectMany(p => Kids(p, "product"))) {
                    if (_productId != null || Attr(product, "name") != "MicrosoftWindows") {
                        continue;
                    }
                    _productId = $"{file.Namespace}:MicrosoftWindows";
                    foreach (var major in Kids(product, "majorVersion")) {
                        var name = Attr(major, "name");
                        if (name == null || !int.TryParse(Attr(major, "versionIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) {
                            continue;
                        }
                        _versionIndex[$"{file.Namespace}:{name}"] = index;
                        foreach (var minor in Kids(major, "minorVersion")) {
                            var minorName = Attr(minor, "name");
                            if (minorName != null) {
                                _versionIndex[$"{file.Namespace}:{minorName}"] = index;
                            }
                        }
                        catalog.WindowsVersions.Add(new WindowsVersion(index, name, file.Res(Attr(major, "displayName") ?? name)));
                    }
                }
            }
            catalog.WindowsVersions.Sort((a, b) => b.Index.CompareTo(a.Index));
        }

        /// <summary>Windows ranges for a supportedOn definition id; null when it names no Windows versions.</summary>
        public List<VersionRange>? GetSupport(string definitionId) {
            if (_productId == null) {
                return null;
            }
            return _cache.GetOrAdd(definitionId, id => {
                var ranges = new List<VersionRange>();
                Collect(id, false, [], ranges);
                return ranges.Count == 0 ? null : ranges.Distinct().ToList();
            });
        }

        private void Collect(string definitionId, bool atLeast, HashSet<string> visited, List<VersionRange> ranges) {
            if (!_definitions.TryGetValue(definitionId, out var entry) || !visited.Add(definitionId)) {
                return;
            }
            var (file, def) = entry;
            // Microsoft never widened some ranges after Windows 11 was added; "At least ..." means open ended.
            var text = Attr(def, "displayName") is { } raw ? ResolveEnglish(file, raw) : "";
            atLeast |= text.TrimStart().StartsWith("At least", StringComparison.OrdinalIgnoreCase);
            foreach (var node in def.Descendants()) {
                var kind = node.Name.LocalName;
                var reference = Attr(node, "ref");
                if (reference == null || kind is not ("range" or "reference")) {
                    continue;
                }
                var id = file.ResolveRef(reference);
                if (kind == "range") {
                    if (!string.Equals(id, _productId, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                    var min = int.TryParse(Attr(node, "minVersionIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lo) ? lo : 0;
                    var max = !atLeast && int.TryParse(Attr(node, "maxVersionIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hi) ? hi : int.MaxValue;
                    ranges.Add(new VersionRange(min, max));
                } else if (string.Equals(id, _productId, StringComparison.OrdinalIgnoreCase)) {
                    ranges.Add(new VersionRange(0, int.MaxValue));
                } else if (_versionIndex.TryGetValue(id, out var index)) {
                    ranges.Add(new VersionRange(index, atLeast ? int.MaxValue : index));
                } else {
                    Collect(id, atLeast, visited, ranges);
                }
            }
        }

        private static string ResolveEnglish(ParsedFile file, string raw) {
            var m = StringRef.Match(raw);
            if (!m.Success || m.Groups[1].Value != "string") {
                return raw;
            }
            var id = m.Groups[2].Value;
            return file.EnStrings.TryGetValue(id, out var v) ? v : file.Strings.TryGetValue(id, out v) ? v : id;
        }
    }

    // ------------------------------------------------------------ definitions

    private static void ParseDefinitions(ParsedFile file, Dictionary<string, string> supportedOn, WindowsInfo windows) {
        var root = file.Admx.Root!;

        if (file.AdmlRoot != null) {
            foreach (var pres in Kids(file.AdmlRoot, "resources").SelectMany(r => Kids(r, "presentationTable")).SelectMany(t => Kids(t, "presentation"))) {
                var id = Attr(pres, "id");
                if (id != null && !file.Presentations.ContainsKey(id)) {
                    file.Presentations[id] = ParsePresentation(id, pres);
                }
            }
        }

        foreach (var c in Kids(root, "categories").SelectMany(c => Kids(c, "category"))) {
            var name = Attr(c, "name");
            if (name == null) {
                continue;
            }
            var parentRef = Attr(Kids(c, "parentCategory").FirstOrDefault(), "ref");
            var explain = Attr(c, "explainText");
            file.Categories.Add(new PolicyCategory {
                Id = $"{file.Namespace}:{name}",
                Name = name,
                DisplayName = file.Res(Attr(c, "displayName") ?? name),
                ExplainText = explain == null ? null : file.Res(explain),
                ParentId = parentRef == null ? null : file.ResolveRef(parentRef)
            });
        }

        foreach (var p in Kids(root, "policies").SelectMany(c => Kids(c, "policy"))) {
            var name = Attr(p, "name");
            var key = Attr(p, "key");
            if (name == null || key == null) {
                file.Warnings.Add($"{file.FileName}: policy without name or key skipped.");
                continue;
            }
            var parentRef = Attr(Kids(p, "parentCategory").FirstOrDefault(), "ref");
            var supRef = Attr(Kids(p, "supportedOn").FirstOrDefault(), "ref");
            var supported = "";
            List<VersionRange>? windowsSupport = null;
            if (supRef != null) {
                windowsSupport = windows.GetSupport(file.ResolveRef(supRef));
                supported = supportedOn.TryGetValue(file.ResolveRef(supRef), out var text) ? text : supRef;
            }

            PolicyPresentation? presentation = null;
            var presRef = Attr(p, "presentation");
            if (presRef != null) {
                var m = StringRef.Match(presRef);
                var presId = m.Success && m.Groups[1].Value == "presentation" ? m.Groups[2].Value : presRef;
                file.Presentations.TryGetValue(presId, out presentation);
            }

            var policy = new PolicyDefinition {
                Namespace = file.Namespace,
                Name = name,
                DisplayName = file.Res(Attr(p, "displayName") ?? name),
                ExplainText = file.Res(Attr(p, "explainText")),
                SupportedOn = supported,
                WindowsSupport = windowsSupport,
                CategoryId = parentRef == null ? null : file.ResolveRef(parentRef),
                SourceFile = file.FileName,
                Class = (Attr(p, "class") ?? "").ToLowerInvariant() switch {
                    "machine" => PolicyClass.Machine,
                    "user" => PolicyClass.User,
                    _ => PolicyClass.Both
                },
                Key = key,
                ValueName = Attr(p, "valueName"),
                EnabledValue = ParseValueChild(Kids(p, "enabledValue").FirstOrDefault()),
                DisabledValue = ParseValueChild(Kids(p, "disabledValue").FirstOrDefault()),
                EnabledList = ParseList(Kids(p, "enabledList").FirstOrDefault()),
                DisabledList = ParseList(Kids(p, "disabledList").FirstOrDefault()),
                Presentation = presentation
            };
            var elements = Kids(p, "elements").FirstOrDefault();
            if (elements != null) {
                foreach (var e in elements.Elements()) {
                    var element = ParseElement(file, e);
                    if (element != null) {
                        policy.Elements.Add(element);
                    }
                }
            }
            file.Policies.Add(policy);
        }
    }

    private static PolicyElement? ParseElement(ParsedFile file, XElement e) {
        var id = Attr(e, "id");
        if (id == null) {
            return null;
        }
        var key = Attr(e, "key");
        var valueName = Attr(e, "valueName");
        switch (e.Name.LocalName) {
            case "boolean":
                return new BooleanElement {
                    Id = id, Key = key, ValueName = valueName,
                    TrueValue = ParseValueChild(Kids(e, "trueValue").FirstOrDefault()),
                    FalseValue = ParseValueChild(Kids(e, "falseValue").FirstOrDefault()),
                    TrueList = ParseList(Kids(e, "trueList").FirstOrDefault()),
                    FalseList = ParseList(Kids(e, "falseList").FirstOrDefault())
                };
            case "decimal":
                return new DecimalElement {
                    Id = id, Key = key, ValueName = valueName,
                    Required = Bool(e, "required"),
                    MinValue = (uint)Math.Min(UInt(e, "minValue", 0), uint.MaxValue),
                    MaxValue = (uint)Math.Min(UInt(e, "maxValue", 9999), uint.MaxValue),
                    StoreAsText = Bool(e, "storeAsText"),
                    Soft = Bool(e, "soft")
                };
            case "longDecimal":
                return new LongDecimalElement {
                    Id = id, Key = key, ValueName = valueName,
                    Required = Bool(e, "required"),
                    MinValue = UInt(e, "minValue", 0),
                    MaxValue = UInt(e, "maxValue", 9999),
                    StoreAsText = Bool(e, "storeAsText"),
                    Soft = Bool(e, "soft")
                };
            case "text":
                return new TextElement {
                    Id = id, Key = key, ValueName = valueName,
                    Required = Bool(e, "required"),
                    MaxLength = (int)Math.Min(UInt(e, "maxLength", 1023), int.MaxValue),
                    Expandable = Bool(e, "expandable"),
                    Soft = Bool(e, "soft")
                };
            case "multiText":
                return new MultiTextElement {
                    Id = id, Key = key, ValueName = valueName,
                    Required = Bool(e, "required"),
                    MaxLength = (int)Math.Min(UInt(e, "maxLength", 1023), int.MaxValue),
                    MaxStrings = (int)Math.Min(UInt(e, "maxStrings", 0), int.MaxValue),
                    Soft = Bool(e, "soft")
                };
            case "enum": {
                var en = new EnumElement { Id = id, Key = key, ValueName = valueName, Required = Bool(e, "required") };
                foreach (var item in Kids(e, "item")) {
                    var value = ParseValueChild(item);
                    if (value == null) {
                        continue;
                    }
                    en.Items.Add(new EnumItem {
                        DisplayName = file.Res(Attr(item, "displayName")),
                        Value = value,
                        ValueList = ParseList(Kids(item, "valueList").FirstOrDefault())
                    });
                }
                return en;
            }
            case "list":
                return new ListElement {
                    Id = id, Key = key, ValueName = valueName,
                    Additive = Bool(e, "additive"),
                    ExplicitValue = Bool(e, "explicitValue"),
                    Expandable = Bool(e, "expandable"),
                    ValuePrefix = Attr(e, "valuePrefix")
                };
            default:
                return null;
        }
    }

    /// <summary>Parses the value node of an enabledValue/disabledValue/trueValue/falseValue/item element.</summary>
    private static PolicyValue? ParseValueChild(XElement? parent) {
        if (parent == null) {
            return null;
        }
        // Enum items wrap the node in <value>; the others hold it directly.
        var holder = Kids(parent, "value").FirstOrDefault() ?? parent;
        var node = holder.Elements().FirstOrDefault(x => x.Name.LocalName is "decimal" or "longDecimal" or "string" or "delete");
        if (node == null) {
            return null;
        }
        return node.Name.LocalName switch {
            "decimal" => new PolicyValue(PolicyValueKind.Decimal, Number(node)),
            "longDecimal" => new PolicyValue(PolicyValueKind.LongDecimal, Number(node)),
            "string" => new PolicyValue(PolicyValueKind.String, 0, node.Value),
            _ => new PolicyValue(PolicyValueKind.Delete)
        };
    }

    private static PolicyValueList? ParseList(XElement? e) {
        if (e == null) {
            return null;
        }
        var list = new PolicyValueList { DefaultKey = Attr(e, "defaultKey") };
        foreach (var item in Kids(e, "item")) {
            var value = ParseValueChild(item);
            if (value == null) {
                continue;
            }
            list.Items.Add(new PolicyListItem(Attr(item, "key"), Attr(item, "valueName") ?? "", value));
        }
        return list;
    }

    // ------------------------------------------------------------ presentation

    private static PolicyPresentation ParsePresentation(string id, XElement pres) {
        var result = new PolicyPresentation { Id = id };
        foreach (var c in pres.Elements()) {
            var refId = Attr(c, "refId");
            var label = Label(c);
            switch (c.Name.LocalName) {
                case "text":
                    result.Controls.Add(new TextLabelControl { Label = label });
                    break;
                case "textBox":
                    result.Controls.Add(new TextBoxControl {
                        RefId = refId, Label = label,
                        DefaultValue = Kids(c, "defaultValue").FirstOrDefault()?.Value ?? Attr(c, "defaultValue")
                    });
                    break;
                case "decimalTextBox":
                    result.Controls.Add(new DecimalTextBoxControl {
                        RefId = refId, Label = label,
                        DefaultValue = UInt(c, "defaultValue", 1),
                        Spin = !string.Equals(Attr(c, "spin"), "false", StringComparison.OrdinalIgnoreCase),
                        SpinStep = UInt(c, "spinStep", 1)
                    });
                    break;
                case "longDecimalTextBox":
                    result.Controls.Add(new LongDecimalTextBoxControl {
                        RefId = refId, Label = label,
                        DefaultValue = UInt(c, "defaultValue", 1),
                        Spin = !string.Equals(Attr(c, "spin"), "false", StringComparison.OrdinalIgnoreCase),
                        SpinStep = UInt(c, "spinStep", 1)
                    });
                    break;
                case "checkBox":
                    result.Controls.Add(new CheckBoxControl { RefId = refId, Label = label, DefaultChecked = Bool(c, "defaultChecked") });
                    break;
                case "comboBox": {
                    var combo = new ComboBoxControl {
                        RefId = refId, Label = label, NoSort = Bool(c, "noSort"),
                        DefaultValue = Kids(c, "default").FirstOrDefault()?.Value ?? Attr(c, "defaultValue")
                    };
                    foreach (var s in Kids(c, "suggestion")) {
                        combo.Suggestions.Add(s.Value);
                    }
                    result.Controls.Add(combo);
                    break;
                }
                case "dropdownList":
                    result.Controls.Add(new DropdownListControl {
                        RefId = refId, Label = label, NoSort = Bool(c, "noSort"),
                        DefaultItem = int.TryParse(Attr(c, "defaultItem"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var di) ? di : null
                    });
                    break;
                case "listBox":
                    result.Controls.Add(new ListBoxControl { RefId = refId, Label = label });
                    break;
                case "multiTextBox":
                    result.Controls.Add(new MultiTextBoxControl {
                        RefId = refId, Label = label,
                        DefaultHeight = (int)Math.Min(UInt(c, "defaultHeight", 3), int.MaxValue)
                    });
                    break;
            }
        }
        return result;
    }

    private static string Label(XElement c) {
        var child = Kids(c, "label").FirstOrDefault();
        if (child != null) {
            return child.Value.Trim();
        }
        return string.Concat(c.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
    }

    // ------------------------------------------------------------ xml helpers

    private static IEnumerable<XElement> Kids(XElement parent, string name) =>
        parent.Elements().Where(e => e.Name.LocalName == name);

    /// <summary>Case-insensitive attribute lookup (a few shipped files use "defaultvalue").</summary>
    private static string? Attr(XElement? e, string name) {
        if (e == null) {
            return null;
        }
        foreach (var a in e.Attributes()) {
            if (string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) {
                return a.Value;
            }
        }
        return null;
    }

    private static bool Bool(XElement e, string name) =>
        string.Equals(Attr(e, name), "true", StringComparison.OrdinalIgnoreCase);

    private static ulong UInt(XElement e, string name, ulong fallback) =>
        ulong.TryParse(Attr(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static ulong Number(XElement node) {
        var text = Attr(node, "value");
        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)) {
            return u;
        }
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? unchecked((ulong)l) : 0;
    }
}

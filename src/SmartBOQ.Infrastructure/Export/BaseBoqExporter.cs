using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.Export;

/// <summary>
/// Abstract base class providing in-place OpenXML archive healing and template sanitization
/// for Excel Bill of Quantities exporters.
/// </summary>
public abstract class BaseBoqExporter : IBoqExporter
{
    public abstract Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    public virtual Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath,
        bool enableDynamicLinking = false,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return ExportPricedBoqAsync(templateFilePath, outputFilePath, matchedPairs, progress, ct);
    }

    public virtual Task ExportPricedBoqAsync(
        string templateFilePath,
        string outputFilePath,
        IReadOnlyList<BoqMatchedPair> matchedPairs,
        string? sourceContractorFilePath,
        bool enableDynamicLinking,
        IEnumerable<string>? knownContractorFilePaths,
        IEnumerable<string>? knownTargetFilePaths,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        return ExportPricedBoqAsync(templateFilePath, outputFilePath, matchedPairs, sourceContractorFilePath, enableDynamicLinking, progress, ct);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Performs in-place OpenXML zip package healing with minimal memory and zero temp-file cloning:
    /// 1. Sanitizes Arabic-Indic digits in core.xml timestamps.
    /// 2. Cleans up broken definedNames in workbook.xml and strips password protection.
    /// 3. Strips complex custom autoFilter rules and unlocks password-protected worksheets.
    /// 4. Removes dangling relationships to missing parts.
    /// </summary>
    protected static void SanitizeOpenXmlPackage(string zipFilePath)
    {
        var prevCulture = Thread.CurrentThread.CurrentCulture;
        var prevUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            using var archive = ZipFile.Open(zipFilePath, ZipArchiveMode.Update);

            var entryNames = new HashSet<string>(
                archive.Entries.Select(e => e.FullName.Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase
            );

            // 1. Sanitize Arabic-Indic dates in docProps/core.xml
            var coreEntry = archive.GetEntry("docProps/core.xml");
            if (coreEntry != null)
            {
                string content;
                using (var reader = new StreamReader(coreEntry.Open(), Encoding.UTF8))
                {
                    content = reader.ReadToEnd();
                }

                bool hasIndic = content.Any(c => c >= '٠' && c <= '٩');
                if (hasIndic)
                {
                    var sb = new StringBuilder(content.Length);
                    foreach (char c in content)
                    {
                        if (c >= '٠' && c <= '٩') sb.Append((char)('0' + (c - '٠')));
                        else sb.Append(c);
                    }

                    coreEntry.Delete();
                    var newCore = archive.CreateEntry("docProps/core.xml", CompressionLevel.Fastest);
                    using var writer = new StreamWriter(newCore.Open(), Utf8NoBom);
                    writer.Write(sb.ToString());
                }
            }

            // 2. Algorithmically sanitize ONLY corrupted / broken defined names from xl/workbook.xml
            var wbEntry = archive.GetEntry("xl/workbook.xml");
            if (wbEntry != null)
            {
                XDocument? doc = null;
                using (var stream = wbEntry.Open())
                {
                    try { doc = XDocument.Load(stream); } catch { }
                }

                if (doc != null)
                {
                    var validSheets = new HashSet<string>(
                        doc.Descendants().Where(e => e.Name.LocalName == "sheet")
                           .Select(s => s.Attribute("name")?.Value ?? "")
                           .Where(s => !string.IsNullOrEmpty(s)),
                        StringComparer.OrdinalIgnoreCase
                    );

                    bool modified = false;
                    var definedNamesElem = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "definedNames");
                    if (definedNamesElem != null)
                    {
                        var namesToRemove = new List<XElement>();
                        foreach (var dn in definedNamesElem.Elements().Where(e => e.Name.LocalName == "definedName"))
                        {
                            string val = dn.Value ?? string.Empty;
                            if (val.Contains("#REF!", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("#VALUE!", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("#NULL!", StringComparison.OrdinalIgnoreCase))
                            {
                                namesToRemove.Add(dn);
                            }
                            else if (val.Contains('!'))
                            {
                                int exclIdx = val.IndexOf('!');
                                string sheetRef = val[..exclIdx].Trim('\'', ' ');
                                if (!string.IsNullOrEmpty(sheetRef) && !validSheets.Contains(sheetRef))
                                {
                                    namesToRemove.Add(dn);
                                }
                            }
                        }

                        if (namesToRemove.Count > 0)
                        {
                            foreach (var dn in namesToRemove) dn.Remove();
                            modified = true;
                        }

                        if (!definedNamesElem.HasElements)
                        {
                            definedNamesElem.Remove();
                            modified = true;
                        }
                    }

                    // Strip password-protected workbookProtection if present
                    var wbProtections = doc.Descendants().Where(e => e.Name.LocalName == "workbookProtection").ToList();
                    if (wbProtections.Count > 0)
                    {
                        foreach (var wp in wbProtections) wp.Remove();
                        modified = true;
                    }

                    if (modified)
                    {
                        wbEntry.Delete();
                        var newWb = archive.CreateEntry("xl/workbook.xml", CompressionLevel.Fastest);
                        using var outStream = newWb.Open();
                        doc.Save(outStream);
                    }
                }
            }

            // 3. Algorithmically inspect and clean autoFilter rules and unlock password-protected sheets
            var sheetEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var sheetEntry in sheetEntries)
            {
                XDocument? doc = null;
                using (var stream = sheetEntry.Open())
                {
                    try { doc = XDocument.Load(stream); } catch { }
                }
                if (doc == null) continue;

                bool modified = false;

                // ClosedXML cannot serialize existing template autoFilter elements and throws NotSupportedException
                var autoFilters = doc.Descendants().Where(e => e.Name.LocalName == "autoFilter").ToList();
                if (autoFilters.Count > 0)
                {
                    foreach (var af in autoFilters) af.Remove();
                    modified = true;
                }

                // Unlock worksheet: remove password-protected sheetProtection so user can edit freely in Excel
                var sheetProtections = doc.Descendants().Where(e => e.Name.LocalName == "sheetProtection").ToList();
                if (sheetProtections.Count > 0)
                {
                    foreach (var sp in sheetProtections) sp.Remove();
                    modified = true;
                }

                if (modified)
                {
                    string sheetPath = sheetEntry.FullName;
                    sheetEntry.Delete();
                    var newSheet = archive.CreateEntry(sheetPath, CompressionLevel.Fastest);
                    using var outStream = newSheet.Open();
                    doc.Save(outStream);
                }
            }

            // 4. Remove dangling relationships
            var relEntries = archive.Entries
                .Where(e => e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var relEntry in relEntries)
            {
                XDocument? doc = null;
                using (var stream = relEntry.Open())
                {
                    try { doc = XDocument.Load(stream); } catch { }
                }

                if (doc != null)
                {
                    string relDir = Path.GetDirectoryName(relEntry.FullName)?.Replace('\\', '/') ?? "";
                    if (relDir.EndsWith("/_rels")) relDir = relDir[..^6];
                    else if (relDir == "_rels") relDir = "";

                    var dangling = new List<XElement>();
                    foreach (var rel in doc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
                    {
                        string? target = rel.Attribute("Target")?.Value;
                        string? targetMode = rel.Attribute("TargetMode")?.Value;

                        if (string.IsNullOrWhiteSpace(target) ||
                            string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase) ||
                            target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                            target.StartsWith("mailto", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string targetClean = target.Trim();
                        string combined = targetClean.StartsWith('/')
                            ? targetClean.TrimStart('/')
                            : (string.IsNullOrEmpty(relDir) ? targetClean : $"{relDir}/{targetClean}");

                        var parts = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var stack = new List<string>(parts.Length);
                        foreach (var p in parts)
                        {
                            if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
                            else if (p != ".") stack.Add(p);
                        }
                        string normalizedTarget = string.Join('/', stack);

                        if (!entryNames.Contains(normalizedTarget))
                        {
                            dangling.Add(rel);
                        }
                    }

                    if (dangling.Count > 0)
                    {
                        foreach (var d in dangling) d.Remove();
                        string relPath = relEntry.FullName;
                        relEntry.Delete();
                        var newRel = archive.CreateEntry(relPath, CompressionLevel.Fastest);
                        using var outStream = newRel.Open();
                        doc.Save(outStream);
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully if in-place archive healing encounters filesystem locks
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prevCulture;
            Thread.CurrentThread.CurrentUICulture = prevUiCulture;
        }
    }
}

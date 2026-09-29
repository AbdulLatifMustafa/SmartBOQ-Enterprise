using System;
using SmartBOQ.Domain.Enums;

namespace SmartBOQ.Domain.Analysis;

/// <summary>
/// Contractual action scope classifier for civil engineering, MEP, infrastructure, and architectural BOQ items.
/// Distinguishes between Supply, Installation/Labor, Comprehensive Supply & Installation,
/// and Demolition/Dismantling/Removal.
/// Enforces strict mutual exclusivity to prevent massive rate misallocations (e.g. matching 650k equipment supply with 65k labor install).
/// </summary>
public static class ContractualScopeClassifier
{
    /// <summary>
    /// Detects contractual scope from item description, hierarchy, and section text.
    /// </summary>
    public static ContractualActionScope DetectScope(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ContractualActionScope.Generic;
        }

        string raw = text;

        // 1. Demolition / Removal / Dismantling
        if (ContainsAny(raw, 
            "dismantle", "dismantling", "demolish", "demolition", "removal of", "stripping out",
            "فك وإزالة", "فك و ازالة", "إزالة", "ازالة", "هدم", "تكسير", "خلع", "تخريد"))
        {
            return ContractualActionScope.Dismantle;
        }

        // Mask noun phrases like "power supply", "air supply", "water supply" to prevent false positive supply detection
        string masked = MaskNounPhrases(raw);

        // 2. Explicit "Only" checks across English and Arabic keywords
        bool hasSupplyOnly = ContainsAny(masked, 
            "supply only", "furnish only", "equipment only", "materials only", "delivery only", "procurement only",
            "توريد فقط", "شراء فقط", "تجهيز فقط", "مهمات فقط", "مواد فقط");

        bool hasInstallOnly = ContainsAny(masked, 
            "install only", "installation only", "erection only", "labor only", "labour only", "fixing only", "wiring only", "termination only",
            "تركيب فقط", "مصنعية فقط", "مصنعيات فقط", "تشغيل فقط", "تثبيت فقط", "توصيل فقط", "سحب فقط", "تمديد فقط");

        if (hasSupplyOnly && !hasInstallOnly) return ContractualActionScope.SupplyOnly;
        if (hasInstallOnly && !hasSupplyOnly) return ContractualActionScope.InstallOnly;

        // 3. Dual Scope: Supply AND Install
        if (ContainsAny(masked,
            "supply and install", "supply & install", "supply and installation", "supply & installation",
            "supply and erect", "supply & erect", "supply, erect", "supply, erection",
            "furnish and install", "furnish & install", "furnish and installation",
            "supply, install", "supply , install", "supply,install",
            "supply, installation", "supply , installation", "supply,installation",
            "supply, test", "supply, testing", "supply and test", "supply & test",
            "supply, deliver and install", "supply, delivery and installation",
            "complete in place",
            "توريد وتركيب", "توريد و تركيب", "توريد وتنفيذ", "توريد و تنفيذ", 
            "توريد وتشغيل", "توريد و تشغيل", "توريد وبناء", "توريد و بناء",
            "توريد وتثبيت", "توريد و تثبيت", "توريد ومد", "توريد و مد",
            "توريد وسحب", "توريد و سحب", "توريد وتركيب وتشغيل", "توريد وتركيب واختبار",
            "بالمصنعية والتوريد", "شامل التوريد والتركيب", "شامل التوريد و التركيب"))
        {
            return ContractualActionScope.SupplyAndInstall;
        }

        // 4. Individual Keyword Checks
        bool hasSupply = ContainsWordOrStem(masked,
            "supply", "supplying", "supply of", "furnish", "furnishing", "furnishing of", "procurement",
            "توريد", "التوريد", "والتوريد", "بالتوريد", "شراء", "تجهيز", "تقديم");

        bool hasInstall = ContainsWordOrStem(masked,
            "install", "installation", "installing", "erection", "fixing", "wiring", "termination", "pulling", "laying",
            "labor", "labour", "workmanship", "testing and commissioning", "testing & commissioning",
            "تركيب", "التركيب", "والتركيب", "بالتركيب", "مصنعية", "مصنعيات", "المصنعية", "بالمصنعية",
            "تثبيت", "توصيل", "تمديد", "سحب كابلات", "سحب أسلاك", "إنهاء وتوصيل", "اختبار وتشغيل");

        if (hasSupply && hasInstall)
        {
            return ContractualActionScope.SupplyAndInstall;
        }

        if (hasSupply)
        {
            return ContractualActionScope.SupplyOnly;
        }

        if (hasInstall)
        {
            return ContractualActionScope.InstallOnly;
        }

        return ContractualActionScope.Generic;
    }

    /// <summary>
    /// Checks whether two contractual scopes are mutually exclusive (e.g. Supply vs Install).
    /// Mutually exclusive pairs MUST NEVER match.
    /// </summary>
    public static bool AreScopesMutuallyExclusive(ContractualActionScope a, ContractualActionScope b)
    {
        if (a == ContractualActionScope.Generic || b == ContractualActionScope.Generic)
        {
            return false;
        }

        // Dismantle is mutually exclusive with new work
        if ((a == ContractualActionScope.Dismantle && b != ContractualActionScope.Dismantle) ||
            (b == ContractualActionScope.Dismantle && a != ContractualActionScope.Dismantle))
        {
            return true;
        }

        // Supply Only and Install Only are strictly mutually exclusive
        if ((a == ContractualActionScope.SupplyOnly && b == ContractualActionScope.InstallOnly) ||
            (a == ContractualActionScope.InstallOnly && b == ContractualActionScope.SupplyOnly))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns a compatibility scoring multiplier (0.0 for incompatible, 1.05 for exact scope match, or penalties for scope imbalance).
    /// </summary>
    public static double GetScopeCompatibilityMultiplier(ContractualActionScope target, ContractualActionScope candidate)
    {
        if (AreScopesMutuallyExclusive(target, candidate))
        {
            return 0.0;
        }

        if (target == ContractualActionScope.Generic || candidate == ContractualActionScope.Generic)
        {
            return 1.0;
        }

        if (target == candidate)
        {
            return 1.05; // Exact scope alignment bonus
        }

        // Labor-only vs Full Supply+Install: massive rate discrepancy
        if ((target == ContractualActionScope.InstallOnly && candidate == ContractualActionScope.SupplyAndInstall) ||
            (target == ContractualActionScope.SupplyAndInstall && candidate == ContractualActionScope.InstallOnly))
        {
            return 0.20; // 80% penalty
        }

        // Supply-only vs Full Supply+Install
        if ((target == ContractualActionScope.SupplyOnly && candidate == ContractualActionScope.SupplyAndInstall) ||
            (target == ContractualActionScope.SupplyAndInstall && candidate == ContractualActionScope.SupplyOnly))
        {
            return 0.50; // 50% penalty
        }

        return 1.0;
    }

    private static string MaskNounPhrases(string text)
    {
        return text
            .Replace("power supply", "power_src", StringComparison.OrdinalIgnoreCase)
            .Replace("power supplies", "power_src", StringComparison.OrdinalIgnoreCase)
            .Replace("air supply", "air_src", StringComparison.OrdinalIgnoreCase)
            .Replace("water supply", "water_src", StringComparison.OrdinalIgnoreCase)
            .Replace("gas supply", "gas_src", StringComparison.OrdinalIgnoreCase)
            .Replace("installation kit", "inst_kit", StringComparison.OrdinalIgnoreCase)
            .Replace("installation manual", "inst_manual", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsWordOrStem(string text, params string[] stems)
    {
        foreach (var stem in stems)
        {
            if (text.Contains(stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

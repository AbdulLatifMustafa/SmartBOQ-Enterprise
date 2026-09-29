using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.CognitiveBrain.Currencies;
using SmartBOQ.Infrastructure.CognitiveBrain.DataStructures;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Engine;

/// <summary>
/// A sheet-agnostic, table-agnostic content signature representing the essence of a BOQ line item.
/// Derived strictly from the description, quantity, rate, unit, and item code.
/// </summary>
public sealed class ItemContentSignature
{
    public BoqItem OriginalItem { get; }
    public CognitiveTokenSet Tokens { get; }
    public DimensionClass Dimension { get; }
    public CurrencyType Currency { get; }
    public string NormalizedCode { get; }
    public decimal Quantity { get; }
    public decimal? Rate { get; }
    public decimal? Amount { get; }
    public ContractualActionScope Scope { get; }
    public ulong FastPathHash { get; }

    public ItemContentSignature(BoqItem item, MultiCurrencyCognitiveArbitrator arbitrator)
    {
        OriginalItem = item;
        string desc = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : item.LineItemText;
        Tokens = CognitiveTokenSet.FromText(desc.AsSpan());
        Dimension = QuantileScaleLattice.ClassifyUnit(item.Unit);
        Currency = arbitrator.ResolveItemCurrency(item);
        NormalizedCode = CleanCode(item.ItemCode);
        Quantity = item.Quantity;
        Rate = item.UnitRate ?? item.OriginalRate;
        Amount = item.TotalAmount;

        string scopeText = $"{desc} {item.SectionName} {item.HierarchyPath}";
        Scope = SmartBOQ.Domain.Analysis.ContractualScopeClassifier.DetectScope(scopeText);

        // Composite 64-bit fast-path hash: allows instant O(1) match if tokens + qty + unit + contractual scope align
        FastPathHash = ComputeFastPathHash(NormalizedCode, Tokens.BloomFilter, Quantity, Dimension, Scope);
    }

    private static ulong ComputeFastPathHash(string code, ulong bloom, decimal qty, DimensionClass dim, ContractualActionScope scope)
    {
        ulong h = bloom;
        if (!string.IsNullOrEmpty(code))
        {
            h ^= CognitiveTokenSet.ComputeHash64(code.AsSpan());
        }
        h ^= (ulong)dim * 1000003UL;
        h ^= (ulong)scope * 5000011UL;
        long qBits = decimal.ToOACurrency(qty);
        h ^= (ulong)qBits;
        return h;
    }

    private static string CleanCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var sb = new System.Text.StringBuilder(code.Length);
        foreach (char c in code)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        return sb.ToString();
    }
}

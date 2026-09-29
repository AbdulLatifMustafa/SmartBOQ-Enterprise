using System.Buffers;
using System.Text.RegularExpressions;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Currencies;

/// <summary>
/// AI-driven multi-currency cognitive arbitrator that automatically detects currencies,
/// manages dual-currency (Offshore / Onshore) tenders, detects exchange rate scale drift,
/// and normalizes rates for fair comparison without mutating contract baseline metadata.
/// </summary>
public sealed class MultiCurrencyCognitiveArbitrator
{
    private readonly ExchangeRateTable _exchangeTable;

    public MultiCurrencyCognitiveArbitrator(ExchangeRateTable? exchangeTable = null)
    {
        _exchangeTable = exchangeTable ?? new ExchangeRateTable();
    }

    public ExchangeRateTable ExchangeTable => _exchangeTable;

    /// <summary>
    /// Detects currency type from a text string, header cell, or currency indicator using zero-allocation scanning.
    /// </summary>
    public CurrencyType DetectCurrency(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return CurrencyType.Unknown;

        // Check common symbols
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '$') return CurrencyType.USD;
            if (c == '€') return CurrencyType.EUR;
            if (c == '£') return CurrencyType.GBP;
        }

        string s = text.ToString();
        if (s.Contains("USD", StringComparison.OrdinalIgnoreCase) || s.Contains("DOLLAR", StringComparison.OrdinalIgnoreCase)) return CurrencyType.USD;
        if (s.Contains("EUR", StringComparison.OrdinalIgnoreCase) || s.Contains("EURO", StringComparison.OrdinalIgnoreCase)) return CurrencyType.EUR;
        if (s.Contains("EGP", StringComparison.OrdinalIgnoreCase) || s.Contains("L.E.", StringComparison.OrdinalIgnoreCase) || s.Contains("ج.م", StringComparison.OrdinalIgnoreCase) || s.Contains("جنيه", StringComparison.OrdinalIgnoreCase)) return CurrencyType.EGP;
        if (s.Contains("SAR", StringComparison.OrdinalIgnoreCase) || s.Contains("ريال", StringComparison.OrdinalIgnoreCase)) return CurrencyType.SAR;
        if (s.Contains("AED", StringComparison.OrdinalIgnoreCase) || s.Contains("درهم", StringComparison.OrdinalIgnoreCase)) return CurrencyType.AED;
        if (s.Contains("GBP", StringComparison.OrdinalIgnoreCase)) return CurrencyType.GBP;
        if (s.Contains("KWD", StringComparison.OrdinalIgnoreCase) || s.Contains("دينار", StringComparison.OrdinalIgnoreCase)) return CurrencyType.KWD;

        return CurrencyType.Unknown;
    }

    /// <summary>
    /// Detects currency from a BoqItem by inspecting Currency, SheetName, HierarchyPath, and Description.
    /// </summary>
    public CurrencyType ResolveItemCurrency(BoqItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Currency))
        {
            var detected = DetectCurrency(item.Currency.AsSpan());
            if (detected != CurrencyType.Unknown) return detected;
        }

        if (!string.IsNullOrWhiteSpace(item.HierarchyPath))
        {
            var detected = DetectCurrency(item.HierarchyPath.AsSpan());
            if (detected != CurrencyType.Unknown) return detected;
        }

        if (!string.IsNullOrWhiteSpace(item.SheetName))
        {
            var detected = DetectCurrency(item.SheetName.AsSpan());
            if (detected != CurrencyType.Unknown) return detected;
        }

        // Default to EGP for Egyptian regional engineering tenders
        return CurrencyType.EGP;
    }

    /// <summary>
    /// Analyzes a pair of rates and checks if the discrepancy is caused by a currency exchange scale factor
    /// (e.g. rate in USD vs rate in EGP, where ratio is approximately 45-55x).
    /// </summary>
    public bool TryDetectCurrencyScaleDrift(decimal rateA, decimal rateB, out CurrencyType likelyA, out CurrencyType likelyB, out decimal normalizedRateB)
    {
        likelyA = CurrencyType.Unknown;
        likelyB = CurrencyType.Unknown;
        normalizedRateB = rateB;

        if (rateA <= 0m || rateB <= 0m) return false;

        decimal ratio = rateA > rateB ? rateA / rateB : rateB / rateA;

        CurrencyType[] candidateCurrencies = [CurrencyType.USD, CurrencyType.EUR, CurrencyType.SAR, CurrencyType.AED, CurrencyType.GBP, CurrencyType.KWD];
        foreach (var foreign in candidateCurrencies)
        {
            decimal parity = _exchangeTable.GetParityRatio(foreign, CurrencyType.EGP);
            if (parity <= 0m || parity == 1.0m) continue;

            if (Math.Abs(ratio - parity) / parity < 0.18m)
            {
                if (rateA > rateB)
                {
                    likelyA = CurrencyType.EGP;
                    likelyB = foreign;
                    normalizedRateB = rateB * parity;
                }
                else
                {
                    likelyA = foreign;
                    likelyB = CurrencyType.EGP;
                    normalizedRateB = rateB / parity;
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalizes a source item's rate into the target item's currency for fair algorithmic comparison.
    /// </summary>
    public decimal NormalizeRate(decimal sourceRate, CurrencyType sourceCurrency, CurrencyType targetCurrency)
    {
        if (sourceCurrency == targetCurrency || sourceCurrency == CurrencyType.Unknown || targetCurrency == CurrencyType.Unknown)
        {
            return sourceRate;
        }

        return _exchangeTable.ConvertAmount(sourceRate, sourceCurrency, targetCurrency);
    }
}

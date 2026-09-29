using System.Collections.Concurrent;

namespace SmartBOQ.Infrastructure.CognitiveBrain.Currencies;

/// <summary>
/// Thread-safe currency exchange rate table and parity registry for multi-currency tender comparison.
/// All rates are stored relative to a base currency (default: EGP).
/// </summary>
public sealed class ExchangeRateTable
{
    private readonly ConcurrentDictionary<CurrencyType, decimal> _ratesAgainstBase = new();
    public CurrencyType BaseCurrency { get; }

    public ExchangeRateTable(CurrencyType baseCurrency = CurrencyType.EGP)
    {
        BaseCurrency = baseCurrency;

        // Default regional engineering baseline rates against EGP
        _ratesAgainstBase[CurrencyType.EGP] = 1.0m;
        _ratesAgainstBase[CurrencyType.USD] = 48.50m;
        _ratesAgainstBase[CurrencyType.EUR] = 52.80m;
        _ratesAgainstBase[CurrencyType.SAR] = 12.93m;
        _ratesAgainstBase[CurrencyType.AED] = 13.20m;
        _ratesAgainstBase[CurrencyType.GBP] = 61.50m;
        _ratesAgainstBase[CurrencyType.KWD] = 158.00m;
        _ratesAgainstBase[CurrencyType.Unknown] = 1.0m;
    }

    public void SetRate(CurrencyType currency, decimal rateAgainstBase)
    {
        if (rateAgainstBase <= 0) throw new ArgumentOutOfRangeException(nameof(rateAgainstBase), "Exchange rate must be positive.");
        _ratesAgainstBase[currency] = rateAgainstBase;
    }

    public decimal GetRateAgainstBase(CurrencyType currency)
    {
        return _ratesAgainstBase.TryGetValue(currency, out var rate) ? rate : 1.0m;
    }

    public decimal ConvertAmount(decimal amount, CurrencyType from, CurrencyType to)
    {
        if (from == to || amount == 0m) return amount;
        decimal fromRate = GetRateAgainstBase(from);
        decimal toRate = GetRateAgainstBase(to);
        if (toRate == 0m) return amount;

        // amount in from -> base -> to
        decimal inBase = amount * fromRate;
        return inBase / toRate;
    }

    public decimal GetParityRatio(CurrencyType from, CurrencyType to)
    {
        if (from == to) return 1.0m;
        decimal fromRate = GetRateAgainstBase(from);
        decimal toRate = GetRateAgainstBase(to);
        return toRate > 0m ? fromRate / toRate : 1.0m;
    }
}

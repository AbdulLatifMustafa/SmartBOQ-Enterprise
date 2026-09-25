namespace SmartBOQ.Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a monetary amount with strict currency adherence (zero FX blending).
/// </summary>
public readonly record struct CurrencyAmount
{
    public decimal Value { get; init; }
    public string Currency { get; init; }

    public CurrencyAmount(decimal value, string currency)
    {
        Value = value;
        Currency = string.IsNullOrWhiteSpace(currency) ? "EGP" : currency.Trim().ToUpperInvariant();
    }

    public static CurrencyAmount ZeroEgp => new(0m, "EGP");
    public static CurrencyAmount ZeroUsd => new(0m, "USD");

    public static CurrencyAmount From(decimal value, string currency) => new(value, currency);

    /// <summary>
    /// Sums two amounts strictly ensuring currencies match to prevent unauthorized cross-currency blending.
    /// </summary>
    public static CurrencyAmount operator +(CurrencyAmount a, CurrencyAmount b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot aggregate disparate currencies ('{a.Currency}' and '{b.Currency}'). Separate currency buckets required.");
        }
        return new CurrencyAmount(a.Value + b.Value, a.Currency);
    }

    public static CurrencyAmount operator *(CurrencyAmount a, decimal multiplier) =>
        new(a.Value * multiplier, a.Currency);

    public override string ToString() => $"{Value:N2} {Currency}";
}

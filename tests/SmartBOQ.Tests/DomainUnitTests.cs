using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Domain.ValueObjects;
using Xunit;

namespace SmartBOQ.Tests;

public class DomainUnitTests
{
    [Fact]
    public void CurrencyAmount_AddSameCurrency_CalculatesCorrectly()
    {
        var a = new CurrencyAmount(100.50m, "EGP");
        var b = new CurrencyAmount(200.25m, "EGP");

        var sum = a + b;

        Assert.Equal(300.75m, sum.Value);
        Assert.Equal("EGP", sum.Currency);
    }

    [Fact]
    public void CurrencyAmount_AddDifferentCurrency_ThrowsInvalidOperationException()
    {
        var egp = new CurrencyAmount(100m, "EGP");
        var usd = new CurrencyAmount(100m, "USD");

        Assert.Throws<InvalidOperationException>(() => egp + usd);
    }

    [Fact]
    public void CurrencyAmount_MultiplyByScalar_CalculatesCorrectly()
    {
        var a = new CurrencyAmount(50m, "SAR");
        var result = a * 3;

        Assert.Equal(150m, result.Value);
        Assert.Equal("SAR", result.Currency);
    }

    [Fact]
    public void BoqItem_IsPriced_ReturnsTrueOnlyForPositiveRates()
    {
        var unpriced = new BoqItem
        {
            Id = "1",
            BillNumber = "01",
            Description = "Excavation",
            UnitRate = null
        };
        var zeroRate = unpriced with { UnitRate = 0m };
        var priced = unpriced with { UnitRate = 125.50m };

        Assert.False(unpriced.IsPriced);
        Assert.False(zeroRate.IsPriced);
        Assert.True(priced.IsPriced);
    }

    [Fact]
    public void BoqItem_IsProtected_ReturnsTrueForProvisionalSums()
    {
        var normal = new BoqItem
        {
            Id = "1",
            BillNumber = "01",
            Description = "Earthworks",
            Type = BoqItemType.Normal
        };
        var ps = normal with { Type = BoqItemType.ProvisionalSum };

        Assert.False(normal.IsProtected);
        Assert.True(ps.IsProtected);
    }

    [Fact]
    public void BoqItem_CalculateTotalScopeAmount_AccountsForQuantityAndNumberOff()
    {
        var item = new BoqItem
        {
            Id = "1",
            BillNumber = "02A",
            Description = "Raft Foundation",
            Quantity = 100m,
            UnitRate = 50m,
            NumberOff = 4,
            Currency = "EGP"
        };

        var total = item.CalculateTotalScopeAmount();

        Assert.Equal(20000m, total.Value); // 100 * 50 * 4 = 20,000
        Assert.Equal("EGP", total.Currency);
    }

    [Fact]
    public void BoqMatchedPair_InjectedRate_ReturnsCustomOrMatchedRate()
    {
        var target = new BoqItem { Id = "T1", BillNumber = "01", Description = "Tiles" };
        var source = new BoqItem { Id = "S1", BillNumber = "01", Description = "Tiles", UnitRate = 45m };

        var pair = new BoqMatchedPair
        {
            TargetItem = target,
            MatchedSourceItem = source,
            SimilarityScore = 1.0,
            Confidence = MatchConfidence.Exact,
            IsApproved = true
        };

        Assert.Equal(45m, pair.InjectedRate);

        // Override custom rate
        pair.InjectedRate = 60m;
        Assert.Equal(60m, pair.InjectedRate);
    }

    [Fact]
    public void BoqMatchedPair_IsVariationOrder_IdentifiesUnmatchedItems()
    {
        var target = new BoqItem { Id = "T1", BillNumber = "01", Description = "Special Item", Type = BoqItemType.Normal };

        var pair = new BoqMatchedPair
        {
            TargetItem = target,
            MatchedSourceItem = null,
            SimilarityScore = 0.0,
            Confidence = MatchConfidence.Unmatched,
            IsApproved = false
        };

        Assert.True(pair.IsVariationOrder);
    }
}

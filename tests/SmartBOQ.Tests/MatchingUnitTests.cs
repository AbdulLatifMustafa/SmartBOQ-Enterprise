using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Matching;
using Xunit;

namespace SmartBOQ.Tests;

public class MatchingUnitTests
{
    private readonly HybridWeightedMatcher _matcher = new();

    [Fact]
    public async Task MatchItemsAsync_ExactDescriptionAndUnit_YieldsExactConfidence()
    {
        var target = new BoqItem
        {
            Id = "T1",
            BillNumber = "01",
            Description = "Excavation in all types of soil",
            Unit = "m3",
            Quantity = 1000m
        };

        var source = new BoqItem
        {
            Id = "S1",
            BillNumber = "01",
            Description = "Excavation in all types of soil",
            Unit = "m3",
            Quantity = 1000m,
            UnitRate = 85.00m
        };

        var results = await _matcher.MatchItemsAsync([target], [source]);

        Assert.Single(results);
        var match = results[0];
        Assert.Equal(MatchConfidence.Exact, match.Confidence);
        Assert.True(match.IsApproved);
        Assert.Equal(85.00m, match.InjectedRate);
    }

    [Theory]
    [InlineData("m2", "sqm")]
    [InlineData("sq.m", "م2")]
    [InlineData("m3", "cum")]
    [InlineData("cu.m", "م3")]
    [InlineData("m", "lm")]
    [InlineData("lin.m", "م.ط")]
    [InlineData("item", "nr")]
    [InlineData("no", "عدد")]
    [InlineData("ton", "tonne")]
    [InlineData("طن", "ton")]
    public async Task MatchItemsAsync_CompatibleUnitsAcrossVariants_MatchesSuccessfully(string targetUnit, string sourceUnit)
    {
        var target = new BoqItem
        {
            Id = "T1",
            BillNumber = "01",
            ItemCode = "A",
            Description = "Granular sub-base layer 150mm thick",
            Unit = targetUnit,
            Quantity = 500m
        };

        var source = new BoqItem
        {
            Id = "S1",
            BillNumber = "01",
            ItemCode = "A",
            Description = "Granular sub-base layer 150mm thick",
            Unit = sourceUnit,
            Quantity = 500m,
            UnitRate = 42.50m
        };

        var results = await _matcher.MatchItemsAsync([target], [source]);

        Assert.Single(results);
        var match = results[0];
        Assert.Equal(85.00m > 0, match.InjectedRate.HasValue);
        Assert.Equal(42.50m, match.InjectedRate);
        Assert.True(match.IsApproved);
    }

    [Fact]
    public async Task MatchItemsAsync_IncompatibleUnits_RejectsMatching()
    {
        var target = new BoqItem
        {
            Id = "T1",
            BillNumber = "01",
            Description = "Supply and install reinforcement steel",
            Unit = "ton", // Weight
            Quantity = 50m
        };

        var source = new BoqItem
        {
            Id = "S1",
            BillNumber = "01",
            Description = "Supply and install reinforcement steel",
            Unit = "m3", // Volume (Incompatible!)
            Quantity = 50m,
            UnitRate = 12000.00m
        };

        var results = await _matcher.MatchItemsAsync([target], [source]);

        Assert.Single(results);
        var match = results[0];
        Assert.Null(match.MatchedSourceItem);
        Assert.Equal(MatchConfidence.Unmatched, match.Confidence);
        Assert.False(match.IsApproved);
    }

    [Fact]
    public async Task MatchItemsAsync_ProvisionalSumItem_ShieldedFromInjection()
    {
        var target = new BoqItem
        {
            Id = "T1",
            BillNumber = "06",
            ItemCode = "6.1",
            Description = "Provisional Sum for Landscape",
            Unit = "sum",
            Quantity = 1m,
            UnitRate = 500000m,
            Type = BoqItemType.ProvisionalSum
        };

        var source = new BoqItem
        {
            Id = "S1",
            BillNumber = "06",
            ItemCode = "6.1",
            Description = "Provisional Sum for Landscape",
            Unit = "sum",
            Quantity = 1m,
            UnitRate = 350000m // Contractor tried to alter PS rate!
        };

        var results = await _matcher.MatchItemsAsync([target], [source]);

        Assert.Single(results);
        var match = results[0];
        Assert.True(match.IsProvisionalSum);
        // Original tender PS rate must NOT be overridden by contractor
        Assert.Equal(500000m, match.TargetItem.UnitRate);
        Assert.Null(match.MatchedSourceItem);
    }

    [Fact]
    public async Task MatchItemsAsync_ArabicDescriptionsWithSynonymsAndCodeMatch_ApprovesRate()
    {
        var target = new BoqItem
        {
            Id = "T1",
            BillNumber = "الباب الأول - أعمال الحفر",
            ItemCode = "1.1",
            Description = "حفر في جميع أنواع التربة حتى مناسيب التأسيس مع نقل ناتج الحفر للمقالب العمومية المعتمدة",
            Unit = "م3",
            Quantity = 1500m
        };

        var source = new BoqItem
        {
            Id = "S1",
            BillNumber = "الباب 1 - أعمال الحفر",
            ItemCode = "1.1",
            Description = "حفر في جميع أنواع التربة الرملية حتى مناسيب التأسيس ونقل ناتج الحفر للمقالب العمومية",
            Unit = "م3",
            Quantity = 1500m,
            UnitRate = 95.00m
        };

        var results = await _matcher.MatchItemsAsync([target], [source]);

        Assert.Single(results);
        var match = results[0];
        Assert.True(match.IsApproved);
        Assert.Equal(95.00m, match.InjectedRate);
    }
}

using System.Collections.ObjectModel;
using System.IO;
using SmartBOQ.App.ViewModels;
using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Common;
using SmartBOQ.Infrastructure.Parsers;
using Xunit;

namespace SmartBOQ.Tests;

/// <summary>
/// Mock coordinator for headless unit testing of child ViewModels
/// without requiring a WPF Application runtime or UI message loop.
/// </summary>
public sealed class MockCoordinator : IMainViewModelCoordinator
{
    public string LastStatus { get; private set; } = string.Empty;
    public int LastProgress { get; private set; } = -1;
    public bool IsBusyState { get; private set; }

    public BoqReconciliationService Service { get; } = null!;
    public ILocalizationService Localization { get; } = null!;
    public IBoqInspector Inspector { get; } = null!;
    public string DatabaseFilePath { get; } = "mock.db";

    public void SetStatus(string message, int progress = -1)
    {
        LastStatus = message;
        LastProgress = progress;
    }

    public void SetBusy(bool isBusy)
    {
        IsBusyState = isBusy;
    }

    public async Task ExecuteWithBusyIndicatorAsync(Func<Task> action, string initialStatus = "Processing...")
    {
        SetBusy(true);
        SetStatus(initialStatus);
        try
        {
            await action();
        }
        finally
        {
            SetBusy(false);
        }
    }
}

public class ViewModelAndResilienceUnitTests
{
    [Fact]
    public void PricingTableViewModel_Pagination_ComputesCorrectSlicesAndPageCounts()
    {
        // Arrange
        var mock = new MockCoordinator();
        var vm = new PricingTableViewModel(mock);
        vm.PageSize = 100;

        var items = new List<BoqMatchedPair>();
        for (int i = 1; i <= 250; i++)
        {
            items.Add(new BoqMatchedPair
            {
                TargetItem = new BoqItem
                {
                    Id = $"T_{i}",
                    BillNumber = "Bill 1",
                    ItemCode = $"1.{i}",
                    Description = $"Item Description {i}",
                    NormalizedDescription = $"item description {i}",
                    Unit = "m2",
                    Quantity = 10m
                },
                MatchedSourceItem = new BoqItem
                {
                    Id = $"S_{i}",
                    BillNumber = "Bill 1",
                    ItemCode = $"1.{i}",
                    Description = $"Source Item Description {i}",
                    NormalizedDescription = $"source item description {i}",
                    Unit = "m2",
                    Quantity = 10m,
                    UnitRate = 100m
                },
                SimilarityScore = 0.95,
                Confidence = MatchConfidence.Exact
            });
        }

        // Act
        vm.LoadMatchedPairs(items);

        // Assert - Page 1
        Assert.Equal(250, vm.TotalFilteredCount);
        Assert.Equal(3, vm.TotalPages);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(100, vm.PagedItems.Count);
        Assert.Equal("1.1", vm.PagedItems[0].TargetItem.ItemCode);
        Assert.Equal("1.100", vm.PagedItems[99].TargetItem.ItemCode);

        // Act - Navigate to Page 2
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(100, vm.PagedItems.Count);
        Assert.Equal("1.101", vm.PagedItems[0].TargetItem.ItemCode);

        // Act - Navigate to Page 3 (Last Page)
        vm.NextPageCommand.Execute(null);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal(50, vm.PagedItems.Count);
        Assert.Equal("1.201", vm.PagedItems[0].TargetItem.ItemCode);
        Assert.Equal("1.250", vm.PagedItems[49].TargetItem.ItemCode);
    }

    [Fact]
    public void PricingTableViewModel_FilterByConfidence_FiltersAccurately()
    {
        // Arrange
        var mock = new MockCoordinator();
        var vm = new PricingTableViewModel(mock);

        var list = new List<BoqMatchedPair>
        {
            new()
            {
                TargetItem = new BoqItem { Id = "T1", BillNumber = "B1", Description = "Item 1", Unit = "m", Quantity = 1m },
                Confidence = MatchConfidence.Exact,
                MatchedSourceItem = new BoqItem { Id = "S1", BillNumber = "B1", Description = "Item 1", Unit = "m", Quantity = 1m, UnitRate = 50m }
            },
            new()
            {
                TargetItem = new BoqItem { Id = "T2", BillNumber = "B1", Description = "Item 2", Unit = "m", Quantity = 1m, Type = BoqItemType.ProvisionalSum },
                Confidence = MatchConfidence.ManualReviewNeeded
            },
            new()
            {
                TargetItem = new BoqItem { Id = "T3", BillNumber = "B1", Description = "Item 3", Unit = "m", Quantity = 1m },
                Confidence = MatchConfidence.ManualReviewNeeded,
                MatchedSourceItem = new BoqItem { Id = "S3", BillNumber = "B1", Description = "Item 3 Alt", Unit = "m", Quantity = 1m, UnitRate = 60m }
            },
            new()
            {
                TargetItem = new BoqItem { Id = "T4", BillNumber = "B1", Description = "Item 4", Unit = "m", Quantity = 1m, Type = BoqItemType.VariationOrder },
                Confidence = MatchConfidence.ManualReviewNeeded
            }
        };

        vm.LoadMatchedPairs(list);

        // Filter: PS (Provisional Sum)
        vm.SetFilter("PS");
        Assert.Equal(1, vm.TotalFilteredCount);
        Assert.Equal("Item 2", vm.PagedItems[0].TargetItem.Description);

        // Filter: VO (Variation Order)
        vm.SetFilter("VO");
        Assert.Equal(1, vm.TotalFilteredCount);
        Assert.Equal("Item 4", vm.PagedItems[0].TargetItem.Description);

        // Filter: Review
        vm.SetFilter("Review");
        Assert.Equal(3, vm.TotalFilteredCount);

        // Filter: All
        vm.SetFilter("All");
        Assert.Equal(4, vm.TotalFilteredCount);
    }

    [Fact]
    public void PricingTableViewModel_ApproveAll_MarksAllPairsApproved()
    {
        // Arrange
        var mock = new MockCoordinator();
        var vm = new PricingTableViewModel(mock);

        var list = new List<BoqMatchedPair>
        {
            new() { TargetItem = new BoqItem { Id = "T1", BillNumber = "B1", Description = "Item 1", Unit = "m", Quantity = 1m }, IsApproved = false },
            new() { TargetItem = new BoqItem { Id = "T2", BillNumber = "B1", Description = "Item 2", Unit = "m", Quantity = 1m }, IsApproved = false }
        };

        vm.LoadMatchedPairs(list);

        // Act
        vm.ApproveAll();

        // Assert
        Assert.All(vm.MatchedPairs, p => Assert.True(p.IsApproved));
    }

    [Theory]
    [InlineData("١٢٥٠.٧٥", 1250.75)]
    [InlineData("۱۲۵۰.۷۵", 1250.75)]
    [InlineData("EGP 4,500.50", 4500.50)]
    [InlineData("$ 1,200", 1200.0)]
    [InlineData("250.00 ج.م", 250.0)]
    [InlineData("125,50", 125.50)]
    [InlineData("#VALUE!", 0.0)]
    [InlineData("#REF!", 0.0)]
    [InlineData("#N/A", 0.0)]
    [InlineData("Rate only", 0.0)]
    [InlineData("بند محمل", 0.0)]
    [InlineData("-", 0.0)]
    [InlineData("", 0.0)]
    public void BaseBoqReader_ParseDecimal_ResilientlyParsesArbitraryNumerals(string input, decimal expected)
    {
        // Act
        decimal result = BaseBoqReader.ParseDecimal(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FileAccessValidator_DetectsNonExistentFileAsNotLocked()
    {
        // Arrange
        string ghostPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");

        // Act
        bool isLocked = FileAccessValidator.IsFileLocked(ghostPath);

        // Assert
        Assert.False(isLocked);
    }

    [Fact]
    public void ProjectSummaryViewModel_ComputesAccuracyAndMetricsAccurately()
    {
        // Arrange
        var mock = new MockCoordinator();
        var vm = new ProjectSummaryViewModel(mock);

        var pairs = new List<BoqMatchedPair>
        {
            new() { TargetItem = new BoqItem { Id = "T1", BillNumber = "B1", Description = "T1", Unit = "m", Quantity = 1m }, Confidence = MatchConfidence.Exact },
            new() { TargetItem = new BoqItem { Id = "T2", BillNumber = "B1", Description = "T2", Unit = "m", Quantity = 1m }, Confidence = MatchConfidence.HighFuzzy },
            new() { TargetItem = new BoqItem { Id = "T3", BillNumber = "B1", Description = "T3", Unit = "m", Quantity = 1m }, Confidence = MatchConfidence.ManualReviewNeeded },
            new() { TargetItem = new BoqItem { Id = "T4", BillNumber = "B1", Description = "T4", Unit = "m", Quantity = 1m }, Confidence = MatchConfidence.ManualReviewNeeded }
        };

        var result = new ReconciliationResult
        {
            MatchedPairs = pairs,
            TargetSheets = [],
            SourceItems = [],
            CurrencySummaries =
            [
                new CurrencyBucketSummary { Currency = "EGP", TotalBaseAmount = 10000m, TotalRemeasureAmount = 9500m }
            ],
            ElapsedTime = TimeSpan.FromSeconds(1.45)
        };

        // Act
        vm.UpdateResult(result);

        // Assert
        Assert.Equal(4, vm.TotalTargetItems);
        Assert.Equal(1, vm.ExactMatchesCount);
        Assert.Equal(1, vm.HighFuzzyCount);
        Assert.Equal(2, vm.ReviewNeededCount);
        Assert.Equal(50.0, vm.MatchAccuracyRate); // (1 + 1) / 4 * 100 = 50.0%
        Assert.Single(vm.CurrencySummaries);
    }

    [Fact]
    public void MultiTarget_FilteringByTargetWorkbook_FiltersCorrectly()
    {
        // Arrange
        var mock = new MockCoordinator();
        var mainVm = new MainViewModel();

        var itemPkg1 = new BoqItem { Id = "T1", BillNumber = "B1", Description = "Pipe 100mm", Unit = "m", Quantity = 50m, WorkbookName = "Pkg_01.xlsx" };
        var itemPkg2 = new BoqItem { Id = "T2", BillNumber = "B1", Description = "Cable 4x16", Unit = "m", Quantity = 100m, WorkbookName = "Pkg_02.xlsx" };

        var pair1 = new BoqMatchedPair { TargetItem = itemPkg1, InjectedRate = 120m, Confidence = MatchConfidence.Exact };
        var pair2 = new BoqMatchedPair { TargetItem = itemPkg2, InjectedRate = 45m, Confidence = MatchConfidence.Exact };

        mainVm.TargetFilterOptions.Add("All Target BOQs (2 items)");
        mainVm.TargetFilterOptions.Add("Pkg_01.xlsx");
        mainVm.TargetFilterOptions.Add("Pkg_02.xlsx");

        // Simulate multi-target reconcile population
        var allPairsField = typeof(MainViewModel).GetField("_allReconciledPairs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var allPairs = (List<BoqMatchedPair>)allPairsField!.GetValue(mainVm)!;
        allPairs.Add(pair1);
        allPairs.Add(pair2);

        // Act 1: Select All
        mainVm.SelectedTargetFilter = "All Target BOQs (2 items)";
        Assert.Equal(2, mainVm.MatchedPairs.Count);
        Assert.True(mainVm.HasMultipleTargets);

        // Act 2: Select Pkg_01.xlsx
        mainVm.SelectedTargetFilter = "Pkg_01.xlsx";
        Assert.Single(mainVm.MatchedPairs);
        Assert.Equal("Pkg_01.xlsx", mainVm.MatchedPairs[0].TargetItem.WorkbookName);

        // Act 3: Select Pkg_02.xlsx
        mainVm.SelectedTargetFilter = "Pkg_02.xlsx";
        Assert.Single(mainVm.MatchedPairs);
        Assert.Equal("Pkg_02.xlsx", mainVm.MatchedPairs[0].TargetItem.WorkbookName);
    }
}

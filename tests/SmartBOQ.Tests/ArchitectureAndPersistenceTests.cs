using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartBOQ.App.Services;
using SmartBOQ.App.ViewModels;
using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Export;
using SmartBOQ.Infrastructure.Logging;
using SmartBOQ.Infrastructure.Matching;
using SmartBOQ.Infrastructure.Parsers;
using SmartBOQ.Infrastructure.Storage;
using SmartBOQ.Infrastructure.Storage.Migrations;
using SmartBOQ.Infrastructure.Verification;

namespace SmartBOQ.Tests;

public class ArchitectureAndPersistenceTests
{
    private class DummyCoordinator : IMainViewModelCoordinator
    {
        public BoqReconciliationService Service { get; }
        public ILocalizationService Localization { get; } = new LocalizationService();
        public IBoqInspector Inspector { get; } = new BoqInspectorService();
        public string DatabaseFilePath { get; }

        public DummyCoordinator(string dbPath)
        {
            DatabaseFilePath = dbPath;
            Service = new BoqReconciliationService(
                new PreFlightVerificationGate(),
                new UniversalAdaptiveBoqReader(),
                new HierarchicalBoqReader(),
                new HybridWeightedMatcher(),
                new ClosedXmlExporter(),
                new SqliteBoqRepository(dbPath),
                Inspector
            );
        }

        public void SetStatus(string message, int progress = -1) { }
        public void SetBusy(bool isBusy) { }
        public async Task ExecuteWithBusyIndicatorAsync(Func<Task> action, string initialStatus = "Processing...") => await action();
    }

    [Fact]
    public async Task DatabaseMigrator_AppliesAllMigrations_UpToTargetVersion()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"test_mig_{Guid.NewGuid():N}.db");
        try
        {
            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();

            await using (var cmdVer = conn.CreateCommand())
            {
                cmdVer.CommandText = "PRAGMA user_version;";
                int initialVersion = Convert.ToInt32(await cmdVer.ExecuteScalarAsync());
                Assert.Equal(0, initialVersion);
            }

            var migrator = new DatabaseMigrator();
            await migrator.MigrateAsync(conn);

            await using (var cmdVer = conn.CreateCommand())
            {
                cmdVer.CommandText = "PRAGMA user_version;";
                int finalVersion = Convert.ToInt32(await cmdVer.ExecuteScalarAsync());
                Assert.Equal(3, finalVersion);
            }

            // Verify tables exist
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Snapshots', 'SnapshotItems', 'MappingPresets', 'ItemAuditTrail');";
            long count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(4L, count);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task SqliteBoqRepository_MappingPresets_CanSaveRetrieveAndDelete()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"test_repo_presets_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteBoqRepository(dbPath);
            await repo.InitializeDatabaseAsync();

            var preset = new MappingPreset
            {
                PresetName = "Contractor_Alpha_Channel",
                ContractorName = "Alpha Construction",
                SourceRateCol = 5,
                TargetRateCol = 8,
                SourceDescCol = 2,
                TargetDescCol = 3,
                SourceCodeCol = 1,
                TargetCodeCol = 1,
                SourceQtyCol = 4,
                TargetQtyCol = 5,
                SourceUnitCol = 3,
                TargetUnitCol = 4
            };

            await repo.SaveMappingPresetAsync(preset);

            var presets = await repo.GetMappingPresetsAsync();
            Assert.Contains(presets, p => p.PresetName == "Contractor_Alpha_Channel" && p.SourceRateCol == 5 && p.TargetRateCol == 8);

            await repo.DeleteMappingPresetAsync("Contractor_Alpha_Channel");

            var afterDelete = await repo.GetMappingPresetsAsync();
            Assert.DoesNotContain(afterDelete, p => p.PresetName == "Contractor_Alpha_Channel");
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task SqliteBoqRepository_AuditTrail_CanRecordAndRetrieve()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"test_repo_audit_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteBoqRepository(dbPath);
            await repo.InitializeDatabaseAsync();

            var auditLog = new ItemAuditLog
            {
                ItemId = "ITEM-1002",
                BillNumber = "Bill 01",
                ItemCode = "01.02.003",
                Description = "Reinforced Concrete Foundation",
                OldRate = 4500m,
                NewRate = 4850m,
                Action = "RateOverride",
                Reason = "Updated to reflect supplier cement index",
                Engineer = "Lead Cost Engineer"
            };

            await repo.RecordAuditLogAsync(auditLog);

            var logs = await repo.GetAuditLogsForItemAsync("ITEM-1002");
            Assert.NotEmpty(logs);
            Assert.Equal("ITEM-1002", logs[0].ItemId);
            Assert.Equal(4500m, logs[0].OldRate);
            Assert.Equal(4850m, logs[0].NewRate);
            Assert.Equal("Lead Cost Engineer", logs[0].Engineer);
            Assert.Equal("RateOverride", logs[0].Action);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task ComparePipelineViewModel_PresetManagement_SavesAndAppliesPreset()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"test_vm_presets_{Guid.NewGuid():N}.db");
        try
        {
            var coordinator = new DummyCoordinator(dbPath);
            var vm = new ComparePipelineViewModel(coordinator);

            // Populate mock columns
            vm.AvailableSourceColumns.Add(new ColumnBindingOption { ColumnIndex = 5, ColumnLetter = "F", HeaderName = "Unit Rate" });
            vm.AvailableTargetColumns.Add(new ColumnBindingOption { ColumnIndex = 9, ColumnLetter = "J", HeaderName = "Target Rate" });

            vm.SelectedSourceRateCol = vm.AvailableSourceColumns[0];
            vm.SelectedTargetRateCol = vm.AvailableTargetColumns[0];
            vm.NewPresetName = "TestStandardPreset";

            await vm.SaveCurrentPresetAsync();

            await vm.RefreshPresetsAsync();
            Assert.Contains(vm.SavedPresets, p => p.PresetName == "TestStandardPreset");

            // Reset columns to null
            vm.SelectedSourceRateCol = null;
            vm.SelectedTargetRateCol = null;

            // Apply the preset
            var preset = vm.SavedPresets.First(p => p.PresetName == "TestStandardPreset");
            vm.ApplyPreset(preset);

            Assert.NotNull(vm.SelectedSourceRateCol);
            Assert.Equal(5, vm.SelectedSourceRateCol.ColumnIndex);
            Assert.NotNull(vm.SelectedTargetRateCol);
            Assert.Equal(9, vm.SelectedTargetRateCol.ColumnIndex);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task CancellationToken_AbortsReconcileExecutionImmediately()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"test_reconcile_cts_{Guid.NewGuid():N}.db");
        try
        {
            var coordinator = new DummyCoordinator(dbPath);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancelled token

            // ReconcileMultiSourceAsync should throw OperationCanceledException immediately
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await coordinator.Service.ReconcileMultiSourceAsync(
                    new[] { "dummyA.xlsx" },
                    "dummyB.xlsx",
                    0.85,
                    ct: cts.Token
                );
            });
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void DependencyInjection_Container_ResolvesAllServicesAndViewModels()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"test_di_{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();

            services.AddSingleton<IVerificationGate, PreFlightVerificationGate>();
            services.AddSingleton<IItemMatcher, HybridWeightedMatcher>();
            services.AddSingleton<IBoqExporter, ClosedXmlExporter>();
            services.AddSingleton<ISqliteRepository>(sp => new SqliteBoqRepository(dbPath));
            services.AddSingleton<IBoqInspector, BoqInspectorService>();
            services.AddSingleton<ILocalizationService>(sp =>
            {
                var loc = new LocalizationService();
                loc.SetCulture("ar-EG");
                return loc;
            });

            services.AddSingleton(sp => new BoqReconciliationService(
                sp.GetRequiredService<IVerificationGate>(),
                new UniversalAdaptiveBoqReader(),
                new HierarchicalBoqReader(),
                sp.GetRequiredService<IItemMatcher>(),
                sp.GetRequiredService<IBoqExporter>(),
                sp.GetRequiredService<ISqliteRepository>(),
                sp.GetRequiredService<IBoqInspector>()
            ));

            services.AddTransient(sp => new MainViewModel(
                sp.GetRequiredService<BoqReconciliationService>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<IBoqInspector>(),
                dbPath
            ));

            var provider = services.BuildServiceProvider();

            var service = provider.GetService<BoqReconciliationService>();
            Assert.NotNull(service);

            var inspector = provider.GetService<IBoqInspector>();
            Assert.NotNull(inspector);

            var mainVm = provider.GetService<MainViewModel>();
            Assert.NotNull(mainVm);
            Assert.NotNull(mainVm.Compare);
            Assert.NotNull(mainVm.Pricing);
            Assert.NotNull(mainVm.Historical);
            Assert.NotNull(mainVm.Summary);
            Assert.NotNull(mainVm.Export);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    [Fact]
    public void ReconciliationDiagnosticLogger_WritesStructuredReportFile()
    {
        var targetItem = new BoqItem
        {
            Id = "T-1",
            BillNumber = "Bill 01",
            ItemCode = "01.01",
            Description = "Excavation in all types of soil",
            Unit = "M3",
            Quantity = 500m
        };

        var sourceItem = new BoqItem
        {
            Id = "S-1",
            BillNumber = "Bill 01",
            ItemCode = "01.01",
            Description = "Excavation in ordinary soil",
            Unit = "M3",
            Quantity = 500m,
            UnitRate = 45.5m,
            Currency = "USD"
        };

        var pairs = new List<BoqMatchedPair>
        {
            new BoqMatchedPair
            {
                TargetItem = targetItem,
                MatchedSourceItem = sourceItem,
                InjectedRate = 45.5m,
                Confidence = MatchConfidence.Exact,
                SimilarityScore = 0.98,
                IsApproved = true
            }
        };

        var curSummary = new CurrencyBucketSummary
        {
            Currency = "USD",
            TotalBaseAmount = 22750m,
            TotalRemeasureAmount = 22750m,
            ItemsCount = 1
        };

        var result = new ReconciliationResult
        {
            MatchedPairs = pairs,
            TargetSheets = new List<BoqSheet>(),
            SourceItems = new List<BoqItem> { sourceItem },
            CurrencySummaries = new List<CurrencyBucketSummary> { curSummary },
            ElapsedTime = TimeSpan.FromMilliseconds(350)
        };

        string logPath = ReconciliationDiagnosticLogger.WriteDiagnosticReport(
            targetFilePath: "C:\\Project\\Tender_BOQ.xlsx",
            sourceFilePaths: new[] { "C:\\Project\\Contractor_Rates.xlsx" },
            result: result,
            exportOutputPath: "C:\\Project\\Reconciled_BOQ.xlsx",
            dashboardOutputPath: "C:\\Project\\Dashboard.xlsx",
            snapshotRevisionId: "REV-2026-TEST");

        Assert.True(File.Exists(logPath));
        string content = File.ReadAllText(logPath);
        Assert.Contains("DIAGNOSTIC REPORT", content);
        Assert.Contains("Tender_BOQ.xlsx", content);
        Assert.Contains("Contractor_Rates.xlsx", content);
        Assert.Contains("USD", content);
        Assert.Contains("SUCCESS", content);
    }
}

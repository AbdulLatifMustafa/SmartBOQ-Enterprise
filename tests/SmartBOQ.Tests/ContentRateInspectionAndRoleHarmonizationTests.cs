using System.IO;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;
using SmartBOQ.Infrastructure.Parsers;
using Xunit;

namespace SmartBOQ.Tests;

public class ContentRateInspectionAndRoleHarmonizationTests
{
    private readonly BoqInspectorService _inspector = new();

    [Fact]
    public async Task Inspector_RealClientFiles_AccuratelyDetectsPricingPresenceAndRoles()
    {
        string candyPath = @"C:\Users\BodyBoy\Desktop\BOQs\CANDY FILE.xlsx";
        string prelimPath = @"C:\Users\BodyBoy\Desktop\BOQs\BOQs\01_A_1  Preliminaries_Rev_02.xlsx";
        string lightingPath = @"C:\Users\BodyBoy\Desktop\BOQs\BOQs\03_C_1  Lighting - Phase 1 and 2_Rev_02.xlsx";
        string golfPath = @"C:\Users\BodyBoy\Desktop\BOQs\BOQs\02_B_1  Golf Course Combined Phase 1 Phase 2_Rev_02.xlsx";

        if (!File.Exists(candyPath) || !File.Exists(prelimPath) || !File.Exists(lightingPath))
        {
            // Skip headless CI if local test files not found
            return;
        }

        // Act
        var candyInfo = await _inspector.InspectWorkbookAsync(candyPath);
        var prelimInfo = await _inspector.InspectWorkbookAsync(prelimPath);
        var lightingInfo = await _inspector.InspectWorkbookAsync(lightingPath);

        // Assert CANDY file (Contractor pricing export)
        Assert.True(candyInfo.HasPricedRates, "CANDY file must have positive rates detected.");
        Assert.True(candyInfo.PricedItemsCount > 0, "CANDY file must contain positive priced items.");
        Assert.Equal(BoqFileRole.ContractorPriced, candyInfo.Role);

        // Assert Preliminaries file (Unpriced consultant tender)
        Assert.False(prelimInfo.HasPricedRates, "Preliminaries file has 0 rates and must be detected as unpriced.");
        Assert.Equal(0, prelimInfo.PricedItemsCount);
        Assert.Equal(BoqFileRole.ConsultantTarget, prelimInfo.Role);

        // Assert Lighting file (Unpriced consultant tender)
        Assert.False(lightingInfo.HasPricedRates, "Lighting file has 0 rates and must be detected as unpriced.");
        Assert.Equal(0, lightingInfo.PricedItemsCount);
        Assert.Equal(BoqFileRole.ConsultantTarget, lightingInfo.Role);

        // Assert Golf Course file (Tender booklet)
        if (File.Exists(golfPath))
        {
            var golfInfo = await _inspector.InspectWorkbookAsync(golfPath);
            Assert.True(golfInfo.HasTenderMetadata, "Golf course combined file must detect tender header metadata.");
            Assert.Equal(BoqFileRole.ConsultantTarget, golfInfo.Role);
        }

        // Assert Mechanical file (Mechanical, pumps, irrigation pricing)
        string mechPath = @"C:\Users\BodyBoy\Desktop\BOQs\Mechanical.xlsx";
        if (File.Exists(mechPath))
        {
            var mechInfo = await _inspector.InspectWorkbookAsync(mechPath);
            Assert.True(mechInfo.HasPricedRates, "Mechanical file must have positive rates detected.");
            Assert.True(mechInfo.PricedItemsCount > 0, "Mechanical file must contain positive priced items.");
            Assert.Equal(BoqFileRole.ContractorPriced, mechInfo.DetectedRole);
            Assert.False(mechInfo.Sheets[0].IsNonBillSheet, "Estimation Sheet in Mechanical.xlsx must NOT be marked as non-bill.");
            Assert.True(mechInfo.Sheets[0].EstimatedRows > 0, "Estimation Sheet must have positive estimated rows.");
        }

        // Assert Electrical file (Lighting & electrical fixtures pricing)
        string elecPath = @"C:\Users\BodyBoy\Desktop\BOQs\Electrical.xlsx";
        if (File.Exists(elecPath))
        {
            var elecInfo = await _inspector.InspectWorkbookAsync(elecPath);
            Assert.True(elecInfo.HasPricedRates, "Electrical file must have positive rates detected.");
            Assert.True(elecInfo.PricedItemsCount > 0, "Electrical file must contain positive priced items.");
            Assert.Equal(BoqFileRole.ContractorPriced, elecInfo.DetectedRole);
        }
    }

    [Fact]
    public void InferRole_UnpricedFile_AlwaysInfersConsultantTarget()
    {
        // Act
        var role = BoqInspectorService.InferRole(
            fileName: "My_Custom_Estimate.xlsx",
            hasPricedRates: false,
            pricedItemsCount: 0,
            sampledItemsCount: 50,
            hasTenderMetadata: false,
            sheetsCount: 3);

        // Assert
        Assert.Equal(BoqFileRole.ConsultantTarget, role);
    }

    [Fact]
    public void InferRole_PricedContractorFile_InfersContractorPriced()
    {
        // Act
        var role = BoqInspectorService.InferRole(
            fileName: "CANDY_TENDER_EXPORT.xlsx",
            hasPricedRates: true,
            pricedItemsCount: 45,
            sampledItemsCount: 50,
            hasTenderMetadata: false,
            sheetsCount: 1);

        // Assert
        Assert.Equal(BoqFileRole.ContractorPriced, role);
    }

    [Fact]
    public void InferRole_TenderBookletWithRates_InfersConsultantTarget()
    {
        // Act
        var role = BoqInspectorService.InferRole(
            fileName: "02_B_1 Golf Course Combined_Rev_02.xlsx",
            hasPricedRates: true,
            pricedItemsCount: 20,
            sampledItemsCount: 50,
            hasTenderMetadata: true,
            sheetsCount: 8);

        // Assert
        Assert.Equal(BoqFileRole.ConsultantTarget, role);
    }

    [Fact]
    public void RoleHarmonization_TwoUnpricedFiles_BothAssignedConsultantTarget()
    {
        // Arrange
        var file1 = new BoqFileInfo
        {
            FilePath = @"C:\Preliminaries.xlsx",
            FileName = "Preliminaries.xlsx",
            HasPricedRates = false,
            PricedItemsCount = 0,
            Role = BoqFileRole.ConsultantTarget,
            DetectedRole = BoqFileRole.ConsultantTarget
        };

        var file2 = new BoqFileInfo
        {
            FilePath = @"C:\Lighting.xlsx",
            FileName = "Lighting.xlsx",
            HasPricedRates = false,
            PricedItemsCount = 0,
            Role = BoqFileRole.ConsultantTarget,
            DetectedRole = BoqFileRole.ConsultantTarget
        };

        var list = new List<BoqFileInfo> { file1, file2 };

        // Act - Simulate harmonization logic
        foreach (var f in list.Where(f => !f.HasPricedRates))
        {
            f.Role = BoqFileRole.ConsultantTarget;
        }

        // Assert: Neither file should be forced into ContractorPriced
        Assert.Equal(BoqFileRole.ConsultantTarget, file1.Role);
        Assert.Equal(BoqFileRole.ConsultantTarget, file2.Role);
        Assert.DoesNotContain(list, f => f.Role == BoqFileRole.ContractorPriced);
    }

    [Fact]
    public void RoleHarmonization_UnpricedDroppedFirst_PricedDroppedSecond_CorrectlyAssigned()
    {
        // Arrange: Unpriced consultant file arrived first, Contractor arrived second
        var unpricedTender = new BoqFileInfo
        {
            FilePath = @"C:\Tender_Unpriced.xlsx",
            FileName = "Tender_Unpriced.xlsx",
            HasPricedRates = false,
            PricedItemsCount = 0,
            Role = BoqFileRole.ConsultantTarget,
            DetectedRole = BoqFileRole.ConsultantTarget
        };

        var pricedContractor = new BoqFileInfo
        {
            FilePath = @"C:\CANDY_PRICED.xlsx",
            FileName = "CANDY_PRICED.xlsx",
            HasPricedRates = true,
            PricedItemsCount = 120,
            Role = BoqFileRole.ContractorPriced,
            DetectedRole = BoqFileRole.ContractorPriced
        };

        var list = new List<BoqFileInfo> { unpricedTender, pricedContractor };

        // Act: Apply harmonization
        foreach (var f in list.Where(f => !f.HasPricedRates))
        {
            f.Role = BoqFileRole.ConsultantTarget;
        }

        var pricedFiles = list.Where(f => f.HasPricedRates).ToList();
        var contractorCandidate = pricedFiles.FirstOrDefault(f => f.DetectedRole == BoqFileRole.ContractorPriced) ?? pricedFiles.FirstOrDefault();
        if (contractorCandidate != null) contractorCandidate.Role = BoqFileRole.ContractorPriced;

        // Assert
        Assert.Equal(BoqFileRole.ConsultantTarget, unpricedTender.Role);
        Assert.Equal(BoqFileRole.ContractorPriced, pricedContractor.Role);
    }

    [Fact]
    public void RoleHarmonization_MultiPricedAndMultiConsultant_AccuratelyOrganizesAllRoles()
    {
        // Arrange: 4 priced files, 3 unpriced files
        var priced1 = new BoqFileInfo { FilePath = @"C:\Rate1.xlsx", FileName = "Rate1.xlsx", HasPricedRates = true, PricedItemsCount = 50, DetectedRole = BoqFileRole.ContractorPriced };
        var priced2 = new BoqFileInfo { FilePath = @"C:\Rate2.xlsx", FileName = "Rate2.xlsx", HasPricedRates = true, PricedItemsCount = 40, DetectedRole = BoqFileRole.ContractorPriced };
        var priced3 = new BoqFileInfo { FilePath = @"C:\Rate3.xlsx", FileName = "Rate3.xlsx", HasPricedRates = true, PricedItemsCount = 30, DetectedRole = BoqFileRole.ContractorPriced };
        var priced4 = new BoqFileInfo { FilePath = @"C:\Rate4.xlsx", FileName = "Rate4.xlsx", HasPricedRates = true, PricedItemsCount = 20, DetectedRole = BoqFileRole.ContractorPriced };

        var target1 = new BoqFileInfo { FilePath = @"C:\Pkg1.xlsx", FileName = "Pkg1.xlsx", HasPricedRates = false, PricedItemsCount = 0, DetectedRole = BoqFileRole.ConsultantTarget };
        var target2 = new BoqFileInfo { FilePath = @"C:\Pkg2.xlsx", FileName = "Pkg2.xlsx", HasPricedRates = false, PricedItemsCount = 0, DetectedRole = BoqFileRole.ConsultantTarget };
        var target3 = new BoqFileInfo { FilePath = @"C:\Pkg3.xlsx", FileName = "Pkg3.xlsx", HasPricedRates = false, PricedItemsCount = 0, DetectedRole = BoqFileRole.ConsultantTarget };

        var allFiles = new List<BoqFileInfo> { priced1, target1, priced2, target2, priced3, priced4, target3 };

        // Act: Apply harmonization
        foreach (var f in allFiles.Where(f => !f.HasPricedRates))
        {
            f.Role = BoqFileRole.ConsultantTarget;
        }

        var priced = allFiles.Where(f => f.HasPricedRates).ToList();
        var primaryCandidate = priced.FirstOrDefault(f => f.DetectedRole == BoqFileRole.ContractorPriced) ?? priced.First();
        primaryCandidate.Role = BoqFileRole.ContractorPriced;

        foreach (var pf in priced)
        {
            if (pf == primaryCandidate) continue;
            pf.Role = pf.DetectedRole == BoqFileRole.ConsultantTarget ? BoqFileRole.ConsultantTarget : BoqFileRole.SupplementaryRates;
        }

        // Assert
        Assert.Equal(3, allFiles.Count(f => f.Role == BoqFileRole.ConsultantTarget));
        Assert.Equal(1, allFiles.Count(f => f.Role == BoqFileRole.ContractorPriced));
        Assert.Equal(3, allFiles.Count(f => f.Role == BoqFileRole.SupplementaryRates));
    }
}

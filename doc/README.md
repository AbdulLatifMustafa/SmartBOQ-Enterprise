# SmartBOQ Enterprise - Technical AI & System Architecture Documentation

Welcome to the internal engineering documentation for **SmartBOQ Enterprise v2.0**, an advanced offline Bill of Quantities (BOQ) reconciliation, automated pricing, and commercial analytics engine built with .NET 10, C# 13, and WPF.

This documentation suite is structured specifically for AI coding assistants, system architects, and senior engineers working on or extending the codebase.

---

## 1. Executive System Overview

### The Engineering Problem
In large-scale civil and architectural construction projects, commercial procurement involves two diverging spreadsheets:
1. **Contractor Priced Tender Schedule(s) (Source Files)**:
   - Denormalized master tender workbooks with variable column arrangements.
   - Contains thousands of line items with bill codes, unit net rates, quantities, and model repetition multipliers (`NumberOff`).
2. **Consultant Pricing Re-Measure Schedule (Target File)**:
   - A complex, multi-sheet hierarchical workbook (e.g. 30+ separate worksheets for individual facilities, villas, or packages).
   - Text is fragmented across multi-row broken blocks, with specific columns for Item Code, Description, Unit, Quantity, Rate, and Amount with native Excel formulas (`=E{row}*G{row}`).

### The SmartBOQ Solution
SmartBOQ Enterprise automates the reconciliation of Source and Target workbooks with:
* **Zero Cloud Latency & 100% Offline Security**: No proprietary project data or tender rates leave the local machine.
* **SIMD-Accelerated Hybrid Text Matching**: Sub-second reconciliation across thousands of items using Inverted Token Indexing, early-exit distance pruning, and AVX2 vectorization.
* **100% Template & Formula Preservation**: Zero alteration of consultant styling, tab colors, fonts, or formula trees.
* **Live Relative Dynamic Cross-Workbook Linking**: Modifying a unit rate in the source contractor file dynamically updates the price and recalculates totals in the consultant schedule in Microsoft Excel without manual re-exports.
* **Smart Compound Range Hyperlinks**: One-click navigation that highlights both the full table item context (`C{row}:S{row}`) and focuses the specific Net Rate cell (`R{row}`).
* **Strict Native Currency Segregation**: Isolated containers for EGP, USD, EUR, and SAR to avoid invalid financial blending.
* **Contractual Shielding**: Automated isolation of Provisional Sums (PS) to prevent accidental rate overwrites.
* **Intelligent Diagnostic Logging**: Automatic generation of structured diagnostic reports in `Log/` capturing file manifests, anomaly recoveries, and troubleshooting steps.

---

## 2. Solution Structure & Projects Map

The solution strictly adheres to Clean Architecture principles:

```
SmartBOQ.slnx
│
├── 1. SmartBOQ.Domain (net10.0)
│   └── Pure domain layer: entities, value objects, domain enums, and interfaces.
│       Zero external dependencies.
│
├── 2. SmartBOQ.Application (net10.0)
│   └── Use cases, orchestration service (BoqReconciliationService), decoupled ViewModel
│       coordinator (IMainViewModelCoordinator), localization, and progress reporting.
│
├── 3. SmartBOQ.Infrastructure (net10.0)
│   └── Adaptive Parsers (UniversalAdaptiveBoqReader, SemanticColumnResolver),
│       Matcher (HybridWeightedMatcher, InvertedTokenIndex, Fastenshtein),
│       Exporter (ClosedXML & OpenXML Package Sanitizers),
│       Diagnostics (ReconciliationDiagnosticLogger),
│       Storage (SQLite via Microsoft.Data.Sqlite), and Verification Gate.
│
├── 4. SmartBOQ.App (net10.0-windows)
│   └── Modern WPF desktop application with decomposed MVVM modules:
│       (ComparisonViewModel, PricingGridViewModel, ProjectSummaryViewModel,
│        HistoricalRatesViewModel, ExportWorkflowViewModel, SheetLinkMappingViewModel),
│       vector iconography, and dynamic Dark/Light themes.
│
├── 5. SmartBOQ.CLI (net10.0)
│   └── High-speed command-line runner, deep automated verification tests, and batch processing.
│
└── 6. SmartBOQ.Tests (net10.0-windows)
    └── 68 comprehensive unit, architecture, and UI validation tests (100% pass rate).
```

---

## 3. Documentation Index

| Document | Focus & Content |
| :--- | :--- |
| **[ARCHITECTURE.md](./ARCHITECTURE.md)** | Layered architecture, decoupled ViewModel subsystem, dependency flows, and memory benchmarks. |
| **[ENGINEERING_STANDARDS.md](./ENGINEERING_STANDARDS.md)** | **Master specifications**: cell capacity limits (1M sweet spot, 5M max), currency isolation, and quality gates. |
| **[DATA_MODELS.md](./DATA_MODELS.md)** | Detailed specification of `BoqItem`, `BoqMatchedPair`, `CurrencyBucketSummary`, and status enums. |
| **[MATCHING_ALGORITHM.md](./MATCHING_ALGORITHM.md)** | Hybrid Weighted Matching algorithm, Inverted Token Index, early-exit pruning, and SIMD Levenshtein. |
| **[EXCEL_PIPELINE.md](./EXCEL_PIPELINE.md)** | OpenXML package sanitization, relative dynamic linking, ClosedXML formatting, and compound union hyperlinks. |
| **[SUPPORTED_FILE_STRUCTURES.md](./SUPPORTED_FILE_STRUCTURES.md)** | Universal Adaptive Reader, Semantic Column Resolver (multilingual), column channels, and anomaly normalizations. |
| **[DEPLOYMENT_GUIDE.md](./DEPLOYMENT_GUIDE.md)** | Single-file publishing (`publish.ps1`/`publish.cmd`), runtime detection, and packaging. |

---

## 4. Key Architectural Standards & Quality Constraints

When modifying or extending this codebase, adhere strictly to these constraints:
1. **Zero Emojis in Enterprise Code, Reports & UI**: All production Excel sheets, generated reports, user-facing dialogs, and code comments must be clean, professional, and free of emojis.
2. **Formula Integrity**: Total columns in consultant sheets must always evaluate via native Excel formulas (`=E{row}*G{row}`). Never overwrite formulas with static values.
3. **No Currency Blending**: Never convert or sum USD/EUR items into EGP using arbitrary exchange rates. Always segregate amounts by currency bucket.
4. **Provisional Sum Protection**: Sheets and line items marked as provisional sums (`IsProtected = true`, `BoqItemType.ProvisionalSum`) must remain contractually shielded from contractor rate injection.
5. **Memory Budget**: All parsers must operate with forward-only streaming (`ExcelDataReader`) to maintain constant memory overhead (<200 MB RAM for 100K-row workbooks).
6. **Automated Quality Gates**: All 68 tests must pass, and Release builds must compile with **0 warnings and 0 errors**.

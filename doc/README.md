# SmartBOQ Enterprise - Technical AI & System Architecture Documentation

Welcome to the internal engineering documentation for **SmartBOQ Enterprise**, an advanced offline Bill of Quantities (BOQ) reconciliation, automated pricing, and commercial analytics engine built with .NET 10, C# 13, and WPF.

This documentation suite is structured specifically for AI coding assistants, system architects, and senior engineers working on or extending the codebase.

---

## 1. Executive System Overview

### The Engineering Problem
In large-scale civil and architectural construction projects (e.g. mega-developments like MODON DP3), commercial procurement involves two diverging spreadsheets:
1. **Contractor Priced Tender Schedule (File A)**:
   - A flat, 20+ column master tender workbook (e.g., `DP3 - Hatchway.xlsx`).
   - Contains 2,000+ line items with bill codes, unit net rates, quantities, and model repetition multipliers (`NumberOff`).
2. **Consultant Pricing Re-Measure Schedule (File B)**:
   - A complex, multi-sheet hierarchical workbook (e.g., `REH.1.xlsx`).
   - Contains 30+ separate worksheets representing individual villa types, townhouses, and facility packages.
   - Text is fragmented across multi-row broken blocks, with specific columns for Item Code, Description, Unit, Quantity, Rate (Column G), and Amount (Column H with native Excel formulas `=E{row}*G{row}`).

### The SmartBOQ Solution
SmartBOQ Enterprise automates the reconciliation of File A and File B with:
* **Zero Cloud Latency & 100% Offline Security**: No proprietary project data or tender rates leave the machine.
* **SIMD-Accelerated Hybrid Text Matching**: Sub-2-second reconciliation of 4,000+ items across 30+ sheets using tokenization, contextual biasing, and exact code mapping.
* **100% Template & Formula Preservation**: Zero alteration of consultant styling, tab colors, fonts, or formula trees.
* **Live Relative Dynamic Cross-Workbook Linking**: Modifying a unit rate in File A dynamically updates the price and recalculates totals in File B in Microsoft Excel without requiring manual re-exports.
* **Smart Compound Range Hyperlinks**: One-click navigation that highlights both the full table item context (`C{row}:S{row}`) and focuses the specific Rate cell (`R{row}`).
* **Strict Native Currency Segregation**: Isolated containers for EGP, USD, and EUR to avoid invalid financial blending.
* **Contractual Shielding**: Automated isolation of Provisional Sums (PS) to prevent accidental rate overwrites.

---

## 2. Solution Structure & Projects Map

The solution uses Clean Architecture principles with strict unidirectional dependencies:

```
SmartBOQ.slnx
│
├── 1. SmartBOQ.Domain (net10.0)
│   └── Pure domain layer: entities, value objects, domain enums, and interfaces.
│       Zero external dependencies.
│
├── 2. SmartBOQ.Application (net10.0)
│   └── Use cases, orchestration service (BoqReconciliationService), localization,
│       and progress reporting.
│
├── 3. SmartBOQ.Infrastructure (net10.0)
│   └── Parsers (ExcelDataReader), Matcher (HybridWeightedMatcher), Exporter (ClosedXML & OpenXML),
│       Storage (SQLite via Microsoft.Data.Sqlite), Verification Gate, and File Logging.
│
├── 4. SmartBOQ.App (net10.0-windows)
│   └── Modern WPF desktop application (MVVM, MahApps.Metro.IconPacks.Lucide, Dark/Light theme).
│
└── 5. SmartBOQ.CLI (net10.0)
    └── High-speed command-line runner, deep automated verification tests, and batch processing.
```

---

## 3. Quick Reference Documentation Index

| Document | Focus & Content |
| :--- | :--- |
| **[ARCHITECTURE.md](./ARCHITECTURE.md)** | Layered architecture, dependency flow, immutability, and memory management. |
| **[DATA_MODELS.md](./DATA_MODELS.md)** | Detailed specification of `BoqItem`, `BoqMatchedPair`, `CurrencyAmount`, and status enums. |
| **[MATCHING_ALGORITHM.md](./MATCHING_ALGORITHM.md)** | Hybrid Weighted Matching algorithm, tokenizers, Levenshtein, Jaccard, and confidence gates. |
| **[EXCEL_PIPELINE.md](./EXCEL_PIPELINE.md)** | OpenXML injection, relative external linking, ClosedXML formatting, and compound union hyperlinks. |
| **[SUPPORTED_FILE_STRUCTURES.md](./SUPPORTED_FILE_STRUCTURES.md)** | Supported file structures, ingestion standards, flat schedule vs multi-sheet BOQ, and compatibility checklist. |
| **[DEPLOYMENT_GUIDE.md](./DEPLOYMENT_GUIDE.md)** | Single-file publishing (`publish.ps1`/`publish.cmd`), runtime detection, and packaging. |

---

## 4. Key Architectural Standards & Constraints

When modifying or extending this codebase, adhere strictly to these constraints:
1. **Zero Emojis in Enterprise Code & UI**: All production Excel sheets, generated reports, user-facing dialogs, and code comments must be clean, professional, and free of emojis.
2. **Formula Integrity**: Column H in consultant sheets must always evaluate via native Excel multiplication formulas (`=E{row}*G{row}`). Never overwrite formulas with static values unless an item is explicitly non-calculating.
3. **No Currency Blending**: Never convert or sum USD/EUR items into EGP using arbitrary exchange rates. Always segregate amounts by currency bucket.
4. **Provisional Sum Protection**: Sheets containing "Provisional", "PS", or marked as provisional sums must remain contractually shielded from contractor rate injection.
5. **Memory Overhead**: All parsers must operate with forward-only streaming (`ExcelDataReader`) to maintain constant $O(1)$ memory overhead (<100 MB RAM for 50MB workbooks).

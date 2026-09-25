# SmartBOQ Enterprise

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C# 13](https://img.shields.io/badge/C%23-13.0-239120?logo=csharp)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![WPF](https://img.shields.io/badge/Platform-WPF%20Desktop-0078D6?logo=windows)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![Architecture](https://img.shields.io/badge/Architecture-Clean%20%2F%20Onion-blue)](#architecture)
[![License](https://img.shields.io/badge/License-Proprietary-red)](#)

> **Enterprise Bill of Quantities (BOQ) Reconciliation & Dynamic Pricing Engine**  
> High-performance offline platform for reconciling contractor tender schedules with consultant pricing workbooks using SIMD-accelerated fuzzy matching and native OpenXML live relative linking.

---

## Overview

In construction procurement and quantity surveying, reconciling contractor bid submissions with consultant re-measure schedules has historically required days of manual copy-pasting, error-prone vlookups, and risk of broken native formulas.

**SmartBOQ Enterprise** automates this end-to-end:
* **High-Speed SIMD Matching**: Reconciles 4,000+ items across 30+ worksheets in **under 2 seconds**.
* **100% Template Preservation**: Retains all original consultant styling, tab colors, print areas, and native formulas (e.g. Column H `=E{row}*G{row}`).
* **Live Relative Cross-Workbook Linking**: Injects native OpenXML external links so that modifying rates in the contractor file dynamically recalculates the consultant workbook in Microsoft Excel.
* **Compound Union Hyperlinks**: Navigates to contractor items with smart multi-cell range selection (`C{row}:S{row},R{row}`), highlighting the complete table record while focusing on the Net Rate cell.
* **Strict Currency Guard**: Automatically segregates currencies (EGP, USD, EUR) to prevent invalid financial blending.
* **Contractual Shielding**: Detects and protects Provisional Sums (PS) from accidental overwrites.

---

## Architecture

The project follows strict Clean / Onion Architecture:

```
SmartBOQ.slnx
├── src/
│   ├── SmartBOQ.Domain/          # Pure entities, records, value objects, and domain enums
│   ├── SmartBOQ.Application/     # Reconciliation workflows, localization, orchestration
│   ├── SmartBOQ.Infrastructure/  # Streaming parsers, SIMD matcher, ClosedXML & OpenXML, SQLite
│   ├── SmartBOQ.App/             # Modern WPF desktop interface (MVVM, MahApps.Metro Lucide)
│   └── SmartBOQ.CLI/             # High-speed headless runner and integration test suite
├── doc/                          # Comprehensive technical AI & architectural documentation
├── publish.cmd                   # Windows 1-click publishing launcher
└── publish.ps1                   # .NET 10 runtime verification & single-file publish automation
```

---

## Technical Documentation

Detailed architectural and algorithmic documentation is available in the [`doc/`](./doc) folder:

* **[doc/README.md](./doc/README.md)**: Master AI & developer orientation guide.
* **[doc/ARCHITECTURE.md](./doc/ARCHITECTURE.md)**: Architectural layers, dependency flow, and benchmarks.
* **[doc/DATA_MODELS.md](./doc/DATA_MODELS.md)**: Specifications for `BoqItem`, `BoqMatchedPair`, and `CurrencyAmount`.
* **[doc/MATCHING_ALGORITHM.md](./doc/MATCHING_ALGORITHM.md)**: Tokenization, Levenshtein, Jaccard, and scoring weights.
* **[doc/EXCEL_PIPELINE.md](./doc/EXCEL_PIPELINE.md)**: OpenXML external linking and compound union hyperlink engine.
* **[doc/DEPLOYMENT_GUIDE.md](./doc/DEPLOYMENT_GUIDE.md)**: Build flags, runtime verification gate, and client distribution.

---

## Getting Started

### Prerequisites
* Windows 10/11 (64-bit)
* [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
* Microsoft Excel 2016 or newer

### Building from Source
```powershell
# Restore dependencies and build entire solution
dotnet build src/SmartBOQ.slnx -c Release
```

### Packaging for Client Distribution
Double-click `publish.cmd` or execute:
```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```
This verifies your system's .NET 10 runtime, bundles a compressed single-file executable (`SmartBOQ.App.exe`, ~70 MB), and packages it directly into `Desktop\SmartBOQ_Client`.

---

## Verification & Integrity Test Suite
To run the automated deep verification pipeline on sample schedules:
```powershell
dotnet run --project src/SmartBOQ.CLI -c Release -- --test-merge "ContractorFile.xlsx" "ConsultantFile.xlsx" "."
```

---

## Author & Proprietary Notice
Developed for enterprise commercial construction and infrastructure procurement. All rights reserved.

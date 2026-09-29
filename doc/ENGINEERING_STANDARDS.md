# SmartBOQ Enterprise - Master Engineering Standards & Specifications

This document defines the architectural standards, performance limits, memory budgets, and quality gates governing **SmartBOQ Enterprise v2.0**.

---

## 1. System Scaling & Memory Budget Guidelines

The following benchmarks establish the operational boundaries of the SmartBOQ engine when processing large-scale construction Bill of Quantities (BOQ) workbooks:

| Operational Zone | Total Cell Count (Cells) | Equivalent Row/Column Scale | Estimated Total Processing Time | Peak RAM Footprint | Performance Rating |
| :--- | :---: | :--- | :---: | :---: | :---: |
| **Zone 1: Ultra-Fast (Sweet Spot)** | **Up to 1,000,000**<br>(1 Million Cells) | 100,000 rows $\times$ 10 columns<br>50,000 rows $\times$ 20 columns | **15 – 35 seconds** | **300 – 600 MB** | 🟢 Zero Latency / Instantaneous |
| **Zone 2: Enterprise Mega-Project** | **1,000,000 – 3,000,000**<br>(3 Million Cells) | 200,000 rows $\times$ 15 columns<br>150,000 rows $\times$ 20 columns | **1.0 – 1.5 minutes** | **1.2 – 1.8 GB** | 🟢 High-Efficiency Stable |
| **Zone 3: Practical Maximum Limit** | **Up to 5,000,000**<br>(5 Million Cells) | 350,000 rows $\times$ 15 columns<br>500,000 rows $\times$ 10 columns | **2.0 – 3.0 minutes** | **2.5 – 3.5 GB** | 🟡 Practical Limit (Requires 16GB RAM) |
| **Zone 4: Absolute Physical Ceiling** | **8,000,000 – 10,000,000**<br>(8 – 10 Million Cells) | 600,000 rows $\times$ 15 columns<br>800,000 rows $\times$ 12 columns | **4.0 – 6.0 minutes** | **4.5 – 6.0 GB** | 🔴 Stress Limit (Excel UI will lag on open) |

### Column Layout Constraints:
* **Recommended Width**: Up to **70 columns** runs with optimal memory allocation.
* **Maximum Structural Limit**: Up to **150 – 200 columns** is supported without parser failure. Any schedule exceeding 150 columns represents time-phased progress billing (IPCs) or cash flows that should be segregated into separate worksheets.

---

## 2. Mathematical & Financial Integrity Standards

### Strict Multi-Currency Isolation (Zero FX Blending)
1. **Never Convert Currencies Implicitly**: The engine strictly forbids converting foreign currencies (e.g. `USD`, `EUR`, `SAR`) into local base currencies (e.g. `EGP`) using arbitrary or static exchange rates.
2. **Segregated Financial Buckets (`CurrencyBucketSummary`)**:
   - Every parsed item retains its explicit currency identifier.
   - Financial totals and variance metrics are computed independently per currency container:
     $$\text{Variance}_{\text{Currency}} = \text{TotalRemeasureAmount}_{\text{Currency}} - \text{TotalBaseAmount}_{\text{Currency}}$$
     $$\text{Variance\%}_{\text{Currency}} = \frac{\text{Variance}_{\text{Currency}}}{\text{TotalBaseAmount}_{\text{Currency}}} \times 100$$
3. **Decimal Precision**: All monetary values are processed using 128-bit high-precision `decimal` types. Floating-point types (`float`, `double`) are strictly prohibited for financial sums.

---

## 3. Resilient Anomaly Normalization Standards

To guarantee that any contractor or consultant file can be ingested without crashes or pre-formatting:

1. **Numeral Digit Normalization**:
   - All Eastern Arabic (`٠, ١, ٢, ٣, ٤, ٥, ٦, ٧, ٨, ٩`) and Persian digits are systematically converted to standard Western European ASCII digits (`0-9`).
2. **European Comma Decimal Normalization**:
   - Single-comma numeric strings (e.g. `125,50` or `0,75`) are detected and normalized to invariant period decimal format (`125.50`, `0.75`).
3. **Currency Token Cleansing**:
   - Embedded currency text (e.g. `450 EGP`, `1200 USD`, `٥٠٠ ج.م`, `$`) is stripped prior to rate parsing while preserving the numerical value.
4. **Formula Error Shielding**:
   - Excel calculation error tokens (`#VALUE!`, `#REF!`, `#DIV/0!`, `#N/A`, `#NAME?`) are safely isolated and treated as unpriced/null items rather than throwing parsing exceptions.
5. **Contractual Note Isolation**:
   - Non-numeric contractual descriptors (`Rate only`, `Included`, `بند محمل`, `N/A`) are captured as notes without corrupting unit rate fields.
6. **Composite Rate Arbitration**:
   - When contractor schedules provide split pricing (`Supply Rate`, `Installation Rate`), the engine prioritizes composite all-in rates (`Total Unit Rate`, `فئة شاملة`). Row total amounts (`Total Amount`, `الإجمالي`) are strictly guarded and never misidentified as unit rates.
7. **Deep Preamble & Dynamic Header Discovery**:
   - Dynamic scan depth extends up to 60 rows to bypass multi-tier corporate headers, administrative logos, and notes, with adaptive fallbacks for unit-less item lists.
8. **Context-Aware Bill Discrimination**:
   - The sheet classifier (`IsNonBillSheet`) distinguishes active bill schedules containing summary terminology (e.g. `Bill 01 - Summary of Earthworks`) from non-bill cover summaries.

---

## 4. Contractual Scope Protection Standards

1. **Provisional Sums (PS - المبالغ الاحتياطية)**:
   - Sheets or line items containing `Provisional`, `PS`, or contractually shielded lump-sum allowances must **never be overwritten** by contractor unit rates.
   - Such items are marked with `IsProtected = true` and `BoqItemType.ProvisionalSum` and highlighted in amber in all audit deliverables.
2. **Variation Orders (VO - بنود مستحدثة)**:
   - Contractor scopes not matching any official consultant line item are classified as Variation Orders.
   - VOs are segregated into a dedicated section of the `Audit_Report` and marked with `IsVariationOrder = true`.

---

## 5. MVVM Architecture & Decoupling Standards

1. **Inversion-of-Control Coordinator (`IMainViewModelCoordinator`)**:
   - Child view models (`ComparisonViewModel`, `PricingGridViewModel`, `ProjectSummaryViewModel`, etc.) must never reference each other directly.
   - All cross-component communication occurs through `IMainViewModelCoordinator` or event properties.
2. **Client-Side Virtualization & Pagination**:
   - Any collection bound to the WPF visual tree with $> 500$ elements must implement paging or UI virtualization.
   - Paging default is **100 items per page**, keeping visual tree memory under 150 MB.
3. **Search & Filter Threading**:
   - Text search and filtering must execute on background threads (`Task.Run` with `AsParallel()` and `CancellationToken`) to ensure the WPF dispatcher remains 100% responsive.

---

## 6. Diagnostic Logging & Audit Standards

1. **Automatic Session Reports**:
   - Every merge, reconciliation, or export operation must write a diagnostic text report to `Log/Reconciliation_{FileName}_{Timestamp}_{Status}.txt`.
2. **Report Contents**:
   - System and host specifications (OS, 64-bit, processor).
   - Ingested and exported file manifests with byte sizes and file-lock verification.
   - Match confidence percentages (Exact, HighFuzzy, ManualReview, Unmatched).
   - Segregated currency summary tables.
   - Anomaly recovery log.
   - Exception stack traces with actionable user troubleshooting steps.
3. **Non-Throwing Logging**:
   - Diagnostic log operations must be wrapped in fail-safe try/catch blocks so logging errors can never disrupt the primary BOQ export flow.

---

## 7. Verification & Quality Gates

Before merging or publishing any code to production:
1. **0 Warnings & 0 Errors**: Solution must build in `Release` mode with zero compiler warnings.
2. **100% Automated Test Pass Rate**: All 110 unit, architecture, and engine tests in `SmartBOQ.Tests` must pass.
3. **Automated XAML Icon Enum Verification**: `AllXamlPackIconLucideKinds_AreValidEnumMembers` test must verify that all Lucide icon names declared in XAML match valid enum members in `MahApps.Metro.IconPacks.Lucide`.

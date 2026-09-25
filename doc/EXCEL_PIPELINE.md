# SmartBOQ Excel Processing Pipeline & Live Linkage Architecture

This document explains the OpenXML, ClosedXML, and Microsoft Excel integration pipelines, including native formula preservation, relative external dynamic linking, and compound union hyperlinks.

---

## 1. Hybrid Exporter Architecture

SmartBOQ uses a hybrid export pipeline combining **ClosedXML** (for rapid structured rendering and styling) with direct **System.IO.Compression / OpenXML DOM manipulation** (for relative external formulas and package repair):

```
Step 1: Copy Consultant Template (REH.1.xlsx -> Output.xlsx)
Step 2: ClosedXML Ingestion & Rate Cell Injections (Column G)
Step 3: Append "Audit_Report" & "Pricing_Linkage_Map" Worksheets
Step 4: Save Base Workbook
Step 5: Low-Level OpenXML Package Transformation (InjectRelativeDynamicLinks)
Step 6: OpenXML Package Sanitization & Repair Elimination
```

---

## 2. Template Purity & Formula Integrity

1. **Non-Destructive Overwrites**:
   * The original 33 sheets of `REH.1.xlsx` are never generated from scratch.
   * SmartBOQ opens the consultant's original file as a template and modifies only the target rate cells (Column G).
2. **Formula Preservation in Column H**:
   * Every consultant worksheet contains native Excel multiplication formulas in Column H (e.g. `=E15*G15`).
   * SmartBOQ never replaces formulas with static numbers. When the rate in Column G is updated, Excel automatically re-evaluates Column H.
3. **Tab Colors & Layouts**:
   * All original fonts, borders, tab colors, and print areas remain 100% intact.

---

## 3. Relative Dynamic External Linking Architecture

### The Client Problem
When a contractor updates their unit prices in `DP3 - Hatchway.xlsx`, the consultant would previously have to re-export the entire project.

### The SmartBOQ Solution
SmartBOQ creates true **relative external workbook links** in OpenXML so that rate changes in File A automatically cascade into File B when both files reside in the same folder.

### Low-Level OpenXML Implementation (`InjectRelativeDynamicLinks`)
1. **Creation of `externalLink1.xml`**:
   An OpenXML external link part is created in `xl/externalLinks/externalLink1.xml` referencing the contractor workbook:
   ```xml
   <externalLink xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
     <externalBook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:id="rId1">
       <sheetNames>
         <sheetName val="Sheet1" />
       </sheetNames>
     </externalBook>
   </externalLink>
   ```
2. **Relationship Binding**:
   `xl/externalLinks/_rels/externalLink1.xml.rels` defines the target file path relatively:
   ```xml
   <Relationship Id="rId1" 
     Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath" 
     Target="DP3%20-%20Hatchway.xlsx" 
     TargetMode="External" />
   ```
3. **Cell Formula Binding**:
   The cell in Column G is converted into an external reference formula:
   ```xml
   <c r="G23" s="42">
     <f>[1]Sheet1!$R$2</f>
     <v>37.7</v>
   </c>
   ```
   * The initial cached value `<v>37.7</v>` allows File B to be opened independently without prompts.
   * If File A is modified in Excel, opening File B updates the value dynamically.

---

## 4. Compound Union Range Hyperlinks

### Engineering Objective
When an engineer audits a price in the `Pricing_Linkage_Map` or `Audit_Report` and clicks the button, jumping to an isolated number (e.g. `R2`) causes disorientation. The engineer needs to see the **entire row context** (Bill, Section, Description, Quantity) while keeping the focus on the **Net Rate**.

### Compound SubAddress Syntax
SmartBOQ uses compound union range references in the Excel `=HYPERLINK` formula:

$$\text{Formula} = \text{=HYPERLINK("DP3 - Hatchway.xlsx\#Sheet1!C}\{\text{row}\}\text{:S}\{\text{row}\}\text{,R}\{\text{row}\}\text{", "[ R}\{\text{row}\}\text{ ] فتح وتحديد السعر والجدول")}$$

### Excel Runtime Behavior:
1. **File Activation**: Excel opens `DP3 - Hatchway.xlsx` and navigates to `Sheet1`.
2. **Multi-Cell Selection**: Excel selects the rectangular range `C{row}:S{row}`, highlighting the entire engineering item row (Bill name, Section, Item Code, Description, Unit, Quantity, NumberOff, Net Rate, and Net Bill Amount).
3. **ActiveCell Focus**: Because `R{row}` is the secondary reference in the union string, Excel sets `ActiveCell` directly on `R{row}` (the price cell has the white focus outline and appears in the formula bar).

```
   Col C    Col D      Col K     Col L            Col N   Col O   Col R      Col S
+--------+---------+----------+---------------+-------+-------+----------+----------+
| Bill 2 | Section | Code "A" | Description.. |  m2   |  351  | [ 37.7 ] | 648,402  |  <- Entire row selected
+--------+---------+----------+---------------+-------+-------+----------+----------+
                                                                   ▲
                                                        ActiveCell Focus (R{row})
```

---

## 5. Audit & Analytics Worksheets

### 5.1 `Pricing_Linkage_Map`
* Dedicated cross-workbook navigation and traceability matrix.
* Columns:
  1. `انتقال للمقايسة`: Jumps directly to the item row in the tender schedule.
  2. `فتح وتحديد سعر المقاول`: Compound union link to contractor file (`C{row}:S{row},R{row}`).
  3. `Tender Sheet / Bill`
  4. `Tender Row`
  5. `Tender Code`
  6. `Tender Description`
  7. `Unit`
  8. `Quantity`
  9. `Contractor File`
  10. `Contractor Sheet`
  11. `Contractor Row`
  12. `Contractor Cell`: Clickable link to contractor cell.
  13. `Contractor Code`
  14. `Contractor Description`
  15. `Unit Rate (EGP)`
  16. `Match Algorithm & Confidence`

### 5.2 `Audit_Report`
* Technical audit trail containing summary KPI scorecards:
  * Total Items Reconciled
  * Exact Matches (100%)
  * Shielded Provisional Sums (PS)
  * New Scope / Variation Orders (VO)
* Segregated variance analysis by currency.

### 5.3 Standalone Executive Dashboard (`DP3_Executive_Dashboard.xlsx`)
* Exported independently via `ExcelDashboardBuilder` for senior executive and commercial presentation.
* Contains KPI scorecards, sheet-by-sheet financial distributions, high-variance alarms, and full traceability tables.

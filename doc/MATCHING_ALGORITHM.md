# SmartBOQ Matching Algorithm & Text Processing Pipeline

This document details the mathematical models, SIMD optimizations, and decision trees powering the `HybridWeightedMatcher`.

---

## 1. Algorithmic Overview

Reconciling items across construction workbooks is challenging because:
* Bill item codes can be duplicated across bills (e.g. Item "A" appears in Bill 02A, 02B, and 03).
* Text descriptions are written with slight variations (e.g., "m2", "Sq.m", "MTR SQ", "sqm").
* Punctuation, spacing, and capitalizations vary between engineers.

The `HybridWeightedMatcher` employs a 4-tier hierarchical matching strategy:

```
[ Tier 1: Exact Item Code + Exact Bill Hierarchy Match ] -> Score: 1.00 (Exact)
                         │ (if no exact code match)
                         ▼
[ Tier 2: Normalized Description Hash + Unit Match ]     -> Score: 1.00 (Exact)
                         │ (if no exact text match)
                         ▼
[ Tier 3: Hybrid Weighted Fuzzy Vector Match ]           -> Score: 0.70 - 0.99
                         │ (if score < 0.70)
                         ▼
[ Tier 4: Variation Order / New Scope Classifier ]       -> Unmatched (VO)
```

---

## 2. Text Normalization Pipeline

Before computing similarity vectors, strings are normalized with zero garbage-collection overhead using `ReadOnlySpan<char>`:

1. **Case Normalization**: Converted to lowercase.
2. **Punctuation Stripping**: Characters such as `;`, `,`, `:`, `.`, `-`, `_`, `(`, `)` are replaced with whitespace.
3. **Number & Dimension Preserving**: Digits are preserved but normalized (e.g., `100mm`, `100 mm` $\to$ `100 mm`).
4. **Stop-word Cleaning**: Removal of generic tender filler words (`the`, `and`, `to`, `including`, `ditto`, `as described`).
5. **Unit Normalization**:
   * `m2`, `sqm`, `sq.m`, `m²` $\to$ `m2`
   * `m3`, `cum`, `cu.m`, `m³` $\to$ `m3`
   * `lm`, `m`, `mtr`, `meter` $\to$ `m`
   * `nr`, `no`, `nos`, `each`, `ea` $\to$ `nr`
   * `kg`, `kgs`, `kilogram` $\to$ `kg`
   * `ton`, `tonne`, `t` $\to$ `ton`
   * `item`, `sum`, `lump sum`, `ls` $\to$ `item`

---

## 3. Weighted Scoring Formula

When falling back to Tier 3 (Fuzzy Vector Match), the composite score $S_{total}$ is calculated as:

$$S_{total} = w_{code} \cdot S_{code} + w_{desc} \cdot S_{desc} + w_{unit} \cdot S_{unit} + w_{context} \cdot S_{context}$$

### Default Weights:
* **Description Weight ($w_{desc} = 0.55$)**: The engineering description carries the primary semantics.
* **Item Code Weight ($w_{code} = 0.20$)**: Matches item codes if structurally aligned.
* **Context/Section Weight ($w_{context} = 0.15$)**: Matches Bill/Section hierarchy (e.g., "Substructure", "Finishes").
* **Unit Weight ($w_{unit} = 0.10$)**: Matches unit of measurement compatibility.

---

## 4. Text Similarity Metrics

### 4.1 Token Jaccard Overlap
Measures the intersection over union of normalized words:

$$J(A, B) = \frac{|A \cap B|}{|A \cup B|}$$

This handles word reordering effectively (e.g., "Plain concrete in footings" vs "Footings plain concrete").

### 4.2 SIMD-Accelerated Levenshtein Distance
For detailed sub-string character edits, `SpanTokenizer` computes the edit distance using vectorized SIMD CPU instructions (`Vector<byte>` / `Vector256<byte>`) to process 32 characters in parallel.

$$S_{edit} = 1.0 - \frac{\text{Levenshtein}(A, B)}{\max(|A|, |B|)}$$

The combined description similarity is:

$$S_{desc} = 0.6 \cdot J(A, B) + 0.4 \cdot S_{edit}$$

---

## 5. Confidence Thresholds & Decision Gate

| Score Range | Classification | Action Taken by System |
| :--- | :--- | :--- |
| **$1.00$** | `MatchConfidence.Exact` | Automatically approved; rate injected. |
| **$\ge 0.85$** | `MatchConfidence.HighFuzzy` | Automatically approved; tagged with similarity percentage. |
| **$0.70 \le S < 0.85$** | `MatchConfidence.ManualReviewNeeded` | Flagged in amber for technical review; rate held pending approval. |
| **$< 0.70$** | `MatchConfidence.Unmatched` | Classified as Variation Order (VO); left unpriced with original allowance. |

---

## 6. Conflict Resolution & Greedy Assignment

To prevent multiple target items from falsely claiming the same contractor line item:
1. Candidate matches are scored and inserted into a priority queue sorted by `Score DESC`.
2. Exact matches within the same Bill take immediate precedence.
3. Once a contractor item is assigned to a target item, it is marked as consumed within that Bill scope unless explicitly defined as a reusable model item (e.g., repeating typical villas).

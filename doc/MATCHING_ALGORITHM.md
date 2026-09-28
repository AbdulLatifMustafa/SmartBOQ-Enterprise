# SmartBOQ Matching Algorithm & Text Processing Pipeline

This document details the mathematical models, algorithmic pruning techniques, SIMD optimizations, and decision trees powering the `HybridWeightedMatcher` engine in **SmartBOQ Enterprise v2.0**.

---

## 1. Algorithmic Challenge in Construction BOQs

Reconciling construction Bill of Quantities schedules at enterprise scale introduces unique computational challenges:
1. **Combinatorial Explosion**: A tender with 50,000 contractor items and 50,000 consultant items yields $2.5 \times 10^9$ possible pairs under brute force.
2. **Duplicated Non-Unique Codes**: Item codes (e.g. `A`, `01.01`, `1`) repeat across different trades, sections, or sheets.
3. **Lexical & Phrasing Discrepancies**:
   - Spacing & Punctuation: `"Reinforced Conc. (C35/45)"` vs `"Reinforced Concrete C35 / 45"`.
   - Word Reordering: `"Footings plain concrete"` vs `"Plain concrete in footings"`.
   - Unit Variants: `M2`, `sq.m`, `Sq. Meter`, `m²`.
4. **Numeral System Shifts**: Eastern Arabic digits (`٠-٩`) mixed with Western European digits (`0-9`).

---

## 2. Multi-Tier Hierarchical Matching Architecture

The `HybridWeightedMatcher` implements a 4-tier hierarchical resolution pipeline:

```
[ Target BOQ Item ]
         │
         ▼
[ Tier 1: Deterministic Exact Match ]
  (Exact Code + Exact Normalized Description + Unit Compatible)
         │ (if no exact candidate found)
         ▼
[ Tier 2: Bill Partitioned Candidate Indexing ]
  (Scoped to Same Bill/Sheet Partition)
         │
         ├── Candidate Count > 128? ──► [ Inverted Token Index Pruning ] ──► Top 40 Candidates
         └── Candidate Count ≤ 128? ──────────────────────────────────────► All Bill Candidates
         │
         ▼
[ Tier 3: Vectorized Hybrid Multi-Metric Scoring ]
  (Trigonometric Cosine + Sorted Hash Jaccard + Early-Exit SIMD Levenshtein)
         │ (if bill has no match or score < Sensitivity)
         ▼
[ Tier 4: Global Inverted Index Fallback ]
  (Multi-Core Parallel Retrieval across entire project corpus)
         │
         ├── Score ≥ 0.85 ──► MatchConfidence.HighFuzzy (Auto-Approved)
         ├── 0.70 ≤ Score < 0.85 ──► MatchConfidence.ManualReviewNeeded (Held for Engineer)
         └── Score < 0.70 ──► MatchConfidence.Unmatched / Variation Order (VO)
```

---

## 3. High-Performance Text Normalization & Tokenization

Text normalization runs with zero heap allocations using `ReadOnlySpan<char>` and stack-allocated buffers:

1. **Numeral Digit Normalization**:
   - Converts Eastern Arabic (`\u0660-\u0669`) and Persian (`\u06F0-\u06F9`) digits directly to ASCII `0-9`.
2. **Punctuation & Noise Stripping**:
   - Replaces non-alphanumeric separators (`;`, `,`, `:`, `.`, `-`, `_`, `(`, `)`, `/`, `\`) with spaces.
3. **Engineering Stop-word Pruning**:
   - Removes boilerplate tender filler tokens (`the`, `and`, `to`, `including`, `ditto`, `as`, `described`, `supply`, `install`).
4. **Canonical Unit Normalization**:
   - `m2`, `sqm`, `sq.m`, `m²` $\to$ `m2`
   - `m3`, `cum`, `cu.m`, `m³` $\to$ `m3`
   - `lm`, `m`, `mtr`, `meter` $\to$ `m`
   - `nr`, `no`, `nos`, `each`, `ea`, `عدد` $\to$ `nr`
   - `kg`, `kgs`, `kilogram`, `كجم` $\to$ `kg`
   - `ton`, `tonne`, `t`, `طن` $\to$ `ton`
   - `item`, `sum`, `lump sum`, `ls`, `مقطوعية`, `جملة` $\to$ `item`

---

## 4. Mathematical Similarity Metrics

When evaluating candidate text vectors, three complementary mathematical formulations are computed:

### 4.1 Trigonometric Cosine Similarity
Computes the angular similarity of token frequency vectors:

$$\text{Cosine}(A, B) = \frac{\mathbf{A} \cdot \mathbf{B}}{\|\mathbf{A}\|_2 \|\mathbf{B}\|_2} = \frac{\sum A_i B_i}{\sqrt{\sum A_i^2} \sqrt{\sum B_i^2}}$$

### 4.2 Sorted Hash Jaccard Similarity
Computes the set intersection over union of 64-bit FNV-1a token hashes in $O(N + M)$ time using a two-pointer linear scan:

$$J(A, B) = \frac{|A \cap B|}{|A \cup B|}$$

### 4.3 AVX2 SIMD-Accelerated Levenshtein Distance
Character-level edit distance computed via `Fastenshtein`:

$$S_{lev} = 1.0 - \frac{\text{Levenshtein}(A, B)}{\max(|A|, |B|)}$$

### 4.4 Early-Exit Length Pruning
Because Levenshtein is $O(L_1 \times L_2)$ per candidate, we evaluate the theoretical maximum score bounded by the length difference $|L_1 - L_2|$:

$$\text{MaxLev}(A, B) = 1.0 - \frac{|L_1 - L_2|}{\max(L_1, L_2)}$$

$$\text{MaxScore} = 0.55 \cdot \text{MaxLev}(A, B) + 0.25 \cdot \text{Cosine}(A, B) + 0.20 \cdot J(A, B)$$

$$\mathbf{\text{If } \text{MaxScore} < \text{Threshold} \implies \text{Skip Levenshtein computation immediately!}}$$

This algorithmic optimization bypasses **80% to 92%** of all edit-distance evaluations across large datasets.

---

## 5. Inverted Token Index (`InvertedTokenIndex`)

For candidate partitions containing $> 128$ items, brute-force linear iteration is replaced by an **Inverted Token Index**:

* **Posting Lists**: A dictionary mapping each 64-bit token hash to a list of source item references:
  $$\text{Index}: \text{hash}(token) \mapsto [ \text{Item}_1, \text{Item}_4, \text{Item}_{29}, \dots ]$$
* **Query Execution**: Given target token hashes $\{h_1, h_2, \dots, h_k\}$, posting lists are intersected, items are scored by token overlap frequency and unit compatibility, and only the **top 40 candidate items** are returned for detailed multi-metric evaluation.
* **Complexity**: Reduces comparison space from $O(N)$ to $O(40) = O(1)$ per target item.

---

## 6. Multi-Factor Disambiguation Fitness

When multiple contractor items exhibit similar text scores, the final candidate is disambiguated by a composite fitness score ($0 \dots 100+$):

$$\text{Fitness} = S_{text} \cdot 100 + \Delta_{code} + \Delta_{unit} + \Delta_{section}$$

* **$\Delta_{code} (+30.0)$**: Awarded if `ItemCode` matches exactly.
* **$\Delta_{unit} (+10.0)$**: Awarded if engineering units are identical.
* **$\Delta_{section} (+10.0)$**: Awarded if parent section titles match.

---

## 7. Multi-Core Parallel Scheduling

Execution scales linearly across all physical and logical CPU threads via `Partitioner.Create`:

```csharp
var partitioner = Partitioner.Create(0, targets.Length, Math.Max(1, targets.Length / (Environment.ProcessorCount * 4)));
Parallel.ForEach(partitioner, parallelOptions, range => {
    // Independent parallel chunk evaluation
});
```

* **Zero Shared State Bottlenecks**: Thread-local hash sets and concurrent collections (`ConcurrentDictionary`, `ConcurrentBag`) prevent lock contention.
* **Cancellation**: `CancellationToken` is checked at partition chunk boundaries for instantaneous user cancellation.

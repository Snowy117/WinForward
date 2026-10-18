# E2E benchmark glossary

This glossary is the canonical vocabulary for the remediation. It defines terms, not mechanisms.

| Term | Definition | Not |
| --- | --- | --- |
| **方案对比 (solution comparison)** | A comparison whose unit is a deployed solution: product + build + transport + configuration + driver stack. A row is a solution. | Not a product ranking. A solution delta includes every declared factor. |
| **产品对比 (product comparison)** | A comparison in which every factor except the product implementation is held identical. Currently only the build-mode pair `wf-aot-opt` vs `wf-fdd-opt` is close; it is still exploratory. | Not a synonym for "row comparison". |
| **回归对比 (regression comparison)** | The same row, same release caliber, across two campaigns. The unit is the same product/build; the question is whether it regressed. | Not a cross-product comparison. |
| **发布口径 (release caliber)** | The triple *(plan file, content hash, declared arm durations)* used for every published table. | Not "whatever plan was deployed". |
| **筛查口径 (screening caliber)** | A shorter plan used to decide whether a change is worth a release run. It is never mixed with the release caliber in one table. | Not a release caliber. |
| **行 (row)** | One deployed solution in a campaign: a product, its build, transport, configuration and plan. | Not a product model. |
| **臂 (arm)** | One workload block inside a row's plan (LAT, LOSS, MIX, ...). | Not a pass and not a row. |
| **pass** | One full traversal of all rows in one session. The independent resampling unit. | Not a sample; not a row. |
| **campaign** | One calibration or release session containing one or more passes. | Not a plan. |
| **参考行 (reference row)** | The row against which opponent deltas and the Holm family are computed. Here: `wf-aot-nativeudp`, the default configuration. | Not the fastest row and not the head row. |
| **变体 (variant)** | A row that differs from the reference by one declared factor. Here: `wf-aot-opt` (opt-in UoT) is a variant of the default row. | Not an opponent. |
| **主族 (primary family)** | The pre-declared set of metrics that may carry an inferential claim: TCP p50, CPU, memory p50, UDP loss. | Not all 21 headline metrics. |
| **探索族 (exploratory family)** | Metrics reported but not used for a decision; no "different/same" claim. | Not a failure. |
| **MDD** | Minimum detectable difference: the smallest difference the campaign can resolve at its noise level. Derived from the calibration band. | Not the business threshold. |
| **band (决策带)** | The pre-registered interval used by the verdict: `B = max(B_business, 1.5 × calibration spread)`. | Not a post-hoc threshold. |
| **`different`** | The confidence interval excludes the band. | Not "p < 0.05" alone. |
| **`same` / `equivalent`** | The confidence interval lies wholly inside a pre-registered equivalence band, in a separately declared TOST family. | Not a degenerate zero-width interval. |
| **`inconclusive`** | The interval straddles the band, or there are too few measured passes. | Not "no difference". |
| **`unevaluable`** | The interval is degenerate (zero variance) or the metric has no resolvable measurement at this n. | Not `same`. |
| **作废 (invalidation)** | A pass or campaign voided by a pre-registered instrument gate; it is archived, not deleted. | Not a silent retry. |
| **attempt** | The ordinal of a campaign on a machine, including discarded and voided attempts. | Not a pass number. |
| **阳性对照 (positive control)** | A run with a known injected degradation, used to prove the instrument can detect it. | Not a calibration run. |
| **`BASE-singbox`** | A control that measures the sing-box hop itself, run once during calibration. | Not a product row. |

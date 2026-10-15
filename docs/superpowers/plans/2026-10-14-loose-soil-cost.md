# Loose soil cost and continuous contact

**Goal:** Reduce repeated manual digging cost without deleting soil or disabling solid-floor protection.

**Scope:** Unity branch `fix/manual-soil-yield-overflow` only. Preserve user terrain/settings edits, truck model, frontend and serial control.

## Approved design

Separate persistent loose-soil accounting from visual particle lifetimes; bound repeated work in an already-used region. Keep full-bucket overflow for manual and automatic cutting. Reduce redundant contact constraints, retaining floor, back-of-bucket and vehicle protection.

## Execution checklist

- [ ] Reproduce repeated deposits and source-reset/reexcavation accounting with EditMode tests; run RED before implementation.
- [ ] Compress ground receipts by source and keep remaining source quantities consistent after reexcavation. Truck landing history remains unchanged because it reconstructs the heap.
- [ ] Remove repeated mesh-array allocation from loose-pile intersection tests; reject distant queries before detailed geometry work.
- [ ] Consolidate landed-soil presentation and local querying, with conserved volume and stable placement; validate repeated-region object growth rather than deleting material at a cap.
- [ ] Diagnose manual force/pose/shell layering, add targeted regression, simplify redundant response while retaining floor and shell safety.
- [ ] Run related EditMode tests, prolonged manual contact and three automatic loads in the existing validation copy. Record actual measurements; do not equate screenshots or unit tests with measured frame-rate improvement.

## Verification

Unity executable: `/home/sk/unity-editor/6000.5.10f1/Editor/Unity`.
Validation project: `/home/sk/Unity/manual-acceptance-5JEDMw`.
Run a single test process at a time; copy Scripts and Tests only after the previous run exits. Keep RED/GREEN XML and logs under Diagnostics. Do not stage ProjectSettings or user-generated TerrainData.

## Progress

Initial inspection confirms unbounded ground receipts, per-query mesh array copies, and per-landing pile creation beyond a 0.5m neighborhood. Contact review runs separately without file writes.

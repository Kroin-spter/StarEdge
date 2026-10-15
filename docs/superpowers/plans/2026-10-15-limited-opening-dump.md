# Limited-opening dump implementation plan

User-approved design: pre-position the arm and bucket over the receiving bed,
then hold boom/arm stationary and open only enough to empty. Do not restore
coordinated compensation during discharge. Preserve overflow and soil caching.

- [x] Add a failing geometric test: a carried upward mouth reaches a forward
  discharge angle without going to the joint end stop; respect joint limits.
- [x] Use the same pose-dependent opening target in deployment, projected tip,
  collision sweep and final bucket-only motion. Prefer a mouth 85 degrees from
  down, already proven to release the real bucket in the manual scene test.
- [x] Check the opening corridor, not only the final tooth location, so early
  emitted soil also remains over the truck. Reposition before flow, not during.
- [x] Run EditMode regressions and real continuous-loading PlayMode test in
  the separate validation project; retain actual gravity and receiver accounting.
- [x] Review the diff and report measured results and any remaining failures.

## Evidence and decisions

- `limited-dump-red.xml`: new limited-opening test failed before implementation.
- `limited-dump-unit.xml`: 49 controller tests passed after limited opening.
- `limited-dump-scene-3.xml`: bucket-only motion emptied the real load, but only
  0.29129 / 0.33587 m³ reached the truck. New 98% real-receipt assertion failed.
- Fixed pre-positioning to centre the whole opening arc. Sample both mouth
  edges before any movement; include outlet velocity and gravity. Keep arm/boom
  stationary during discharge. Do not redirect emitted parcels or count spills
  as loaded soil.
- Review caught obsolete final-tip centering rejecting otherwise valid opening
  corridors. Removed that redundant filter; retained entity collision and height
  guards. `limited-dump-corridor-2.xml`: 3 cycles, 1.00762 m³, passed.
- Review also caught horizontal-plane flight timing on inclined receivers.
  `limited-dump-plane-red.xml`: two new analytic tests failed before the helper;
  both pass with complete ballistic intersection against the actual bed plane.
- `limited-dump-final-scene.xml`: final source passes 3 cycles, 1.00762 m³;
  each cycle >=98% actual receipt; dumping 5.40 / 5.36 / 5.38 simulated seconds;
  no commanded boom/arm correction during Dump, physical drift below 3 degrees.
- `limited-dump-all-unit.xml`: 381 total, 367 passed, 12 failed, 2 skipped.
  Core six suites (automatic, fill, cutting, resistance, unloading, joint control)
  pass 174/174. Full-suite failures are separately tracked, not hidden or relaxed.
- Work remains in actual `HMI_Unity6`, branch `fix/manual-soil-yield-overflow`.
  No merge, push, scene layout change, frontend change or package export.
- `limited-dump-manual-final.xml`: 4/4 real-scene tests passed (25/45/65-degree
  digging plus forward-tilt unloading). Measured manual mouth 84.78 degrees:
  0.085087 m³ before, zero after, actual gravitational discharge.
- Read-only independent review: no important issues remain after plane fix.
  `git diff --check` clean; validation controller matches actual project source.
- Baseline check substituted only the HEAD controller in the separate validation
  project, then restored the final controller. Selected 63 tests: 51 passed,
  10 failed, 2 skipped. Ten full-suite failures also occur with the old controller:
  pressure depth, old mild-tilt retention expectation, saved site layout, three
  missing support-blend cases, truck asset, Linux build, two missing HR35 resource
  cases. Two saved-pose failures did not reproduce in that smaller run; their
  cause remains unverified. No unrelated assertions were weakened to hide these.

## Visual posture correction after user review

- The first automatic screenshot still showed an almost horizontal stick and a
  long falling-soil column. A real-scene posture assertion failed: arm pivot was
  0.17 m *below* bucket pivot; teeth were 5.51 m above the receiving surface.
- Geometric investigation found feasible low joint poses, but the old planner
  curled the carry mouth maximally upward. At low height its starting outlet
  landed 2.34 m outside the bed before opening, so the complete-flow guard
  rejected it. The existing small stick search also omitted much of the
  joint's 34–154 degree travel.
- Before enabling flow, pre-position the bucket with its mouth 105 degrees
  from down; then open only to the established 85-degree release angle. Search
  the full boom/stick limits, rank actual bed clearance, and require both a
  visibly lowered stick (pivot drop >=0.5 m) and teeth <=1.5 m above the bed.
  The 1.5 m bound preserves the saved truck's rim clearance: its receiving
  surface is at -0.62 m and the required truck-entity clearance is 0.61 m.
- `low-dump-final.xml`: three real-scene cycles passed and delivered 1.00762 m³.
  Starting dump teeth were 1.30, 1.25, and 1.21 m above the receiving surface;
  the bucket joint was 2.53 m below the arm joint. Each cycle still met the 98%
  real receipt assertion with stationary boom/stick during discharge.
- Visual capture: `Diagnostics/low-dump-preposition.png` in the separate
  validation project. Read-only inspection shows the bucket immediately above
  the bed and a downward stick, unlike the reported high horizontal pose.

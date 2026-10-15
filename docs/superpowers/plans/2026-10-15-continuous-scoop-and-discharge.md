# Continuous scoop, discharge corridor and loose-soil settling

## Current decision

The user rejected the coordinated opening motion as visually unnatural and
explicitly requested restoration of the preceding dump sequence. All new
opening compensation, ballistic planning and associated 98% corridor assertions
have been reverted. The automatic-controller diff now contains only the actual-
fill-based scoop lift change. The discharge investigation below is historical,
not a description of the currently active unloading motion. A minimal pre-dump
position adjustment is deferred, rather than reintroducing linkage compensation.

Scope: actual HMI_Unity6 checkout, branch `fix/manual-soil-yield-overflow`.
Preserve manual unloading geometry, full-bucket overflow, terrain layout, truck
placement, existing user settings and unrelated texture changes.

## Evidence and changes

- Predicted 150% fill with only 30% actual fill commanded 0.35 boom lift.
  Regression reproduced this. Prediction now advances curl only; gentle boom
  assistance starts at 90% actual fill and reaches 0.15 at full capacity.
- Final open-tip alignment did not cover the full emission stroke. A first
  angle-based corridor approximation also failed: runtime measured soil flow
  at 179.69 degrees from gravity immediately upon enabling the loaded cavity,
  with a predicted outlet as much as 0.738 m outside the bed. Therefore no
  angle-based exclusion is safe. Check all outlet positions throughout opening.
- Plan ballistic landing including outlet velocity, receiver inclination and
  both mouth edges. Aim the whole stroke into the bed; allow coordinated
  boom/arm movement while opening. Correct the future opening trajectory for
  measured positioning error rather than repeatedly correcting the closed pose.
- Pressure spoil remains loose near the bucket. After eight seconds without a
  nearby bucket or new deposit, at most one pile per 0.25 s transfers up to
  0.002 m3 to its own stable Terrain. Only actual quantized deposits reduce the
  pile inventory. Do not create a second delivery receipt. Unsettleable soil
  remains loose; empty objects remain reusable.

## Verification

Use the existing isolated validation copy `/home/sk/Unity/manual-acceptance-5JEDMw`.
EditMode covers adaptive weights, conserved settling,
delayed settling/proximity protection and existing excavation/unloading tests.
The experimental whole-stroke version passed a 98% per-dump receipt test, but
that version and its assertion are no longer active following the user's rollback.
Retain original manual contact/tilted-release and handoff scene regressions.
Results and diagnostics are in that copy's `Diagnostics` directory.

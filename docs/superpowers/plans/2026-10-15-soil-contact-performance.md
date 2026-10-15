# Soil contact performance

The user reports low frame rate when digging; dump-motion redesign is out of
scope after the explicit request to restore the preceding opening sequence.

## Measured hotspot

Profiler markers in the actual saved scene isolate ShellGuard, BladeSweep,
PressureYield, Flight and ReceivingMesh. The blocked shell solver dominates,
not ground-pile rendering. A native-query baseline with 798 guard invocations
measured 44.064 ms/call and 84.584 ms active-frame p95. These are test-machine
measurements, not a promised end-user frame rate.

## Implementation

- Capture a bounded height/hole patch once per shell constraint solve. Refresh
  it immediately after an actual pressure cut. Queries outside the patch retain
  the original native fallback.
- Match Terrain.SampleHeight triangle interpolation, not bilinear height
  interpolation; tests compare against native sampling across an uneven surface,
  holes and outside-cache points. Preserve the original bilinear prepared floor.
- Reuse current contact depths and each candidate's transformed points, depths
  and floor values between overlap relief and per-point contact guards. Do not
  relax shell/floor tolerances or reduce contact sampling.
- Retain actual soil collection, overflow and conservative delayed settlement.
  Settling additionally requires contact with the owning Terrain collider, not
  merely matching horizontal coordinates (no transfer through vehicles/air).
- Keep named Soil.* profiler markers for repeatable follow-up measurements.

Validation copy: `/home/sk/Unity/manual-acceptance-5JEDMw`.
Results: `Diagnostics/soil-performance-*` and `Diagnostics/soil-snapshot-*`.
Do not use reverted experimental discharge results to validate the restored motion.

## Measured result

`soil-performance-manual-final.xml`: all four saved-scene manual cases passed
(25/45/65-degree intake, escape from soil, conservation, and forward-tilt discharge).
For the 65-degree case, the final implementation measured 1.567 ms/call and
3.237 ms active-frame p95 across the same 798 guard invocations used by the
native-query baseline. Peak full-frame guard cost was 32.042 ms; this is a major
hotspot reduction, not a claim that every frame of the whole scene stays below
16.7 ms. The 25/45-degree cases measured 0.960/1.341 ms per guard invocation.

The baseline was run only in the validation copy by bypassing cached Depth/Floor
queries; the actual HMI_Unity6 checkout retained the optimized implementation.

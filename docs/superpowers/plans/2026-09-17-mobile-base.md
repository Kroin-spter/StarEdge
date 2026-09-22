# Main scene mobile base implementation plan

Goal: Enable existing force-driven tracks in sence_test and validate excavation after driving.
Architecture: Reuse Zx200TrackDrive, articulation gravity/contact and parking friction; set main scene fixedBase false and preserve this in site generation. No transform teleportation or automatic base locking. W/S forward/reverse, A/D steer.
Alternatives: automatic fixed-base parking would hide reaction stability; individual track links add unnecessary solver cost. Existing contact-force drive is preferred.

- [x] Add real-scene drive, brake, scoop, lift and conservation regression; confirm fixed scene fails.
- [x] Enable dynamic main base and site generator; diagnose only demonstrated stability issues.
- [x] Run track physics and main excavation regression; inspect rendered evidence.
- [x] Back up, compare and sync scoped changes; document limitations and controls.

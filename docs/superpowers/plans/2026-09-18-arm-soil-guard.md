# Arm movement at soil contact

User reports arm cutting often locks at the pictured posture. HUD shows 0.192 m penetration (limit 0.18 m) and reverse scraping. Investigate and correct command rejection rather than increasing depth limits or disabling ground protection.

The existing guard blocks every non-upward command at excessive depth, and a rejected earlier joint contaminates later joints through IsBlocked. Evaluate each requested joint against contact limits independently. Main-scene measurements show the arm cuts at approximately 32 degrees below horizontal (direction Y=-0.531), correctly aligned with the mouth. Permit mouth-leading oblique face cutting up to 45 degrees below horizontal at the existing restricted cutting speed, even at excessive overhead depth. Preserve floor/full-bucket limits, steep downward penetration blocking, reverse/side scraping restrictions and lifting escape. Keep cutting geometry and volume accounting unchanged. This angle is a simplified operational constraint, not a calibrated soil constitutive model.

- Reproduce contact-depth and cross-joint blocking using regression tests.
- Verify actual main-scene down-entry followed by arm retraction and existing full/floor guard tests.
- Run EditMode regressions and relevant rendered PlayMode tests in isolated project.
- Back up scoped main files and deliver only verified changes; record limitations.

Completed: 153 EditMode tests passed (/tmp/arm-oblique-edit.xml); four rendered PlayMode tests passed (/tmp/arm-oblique-play.xml). Main down-entry followed by arm-only retraction advanced from 68.141 to 74.356 degrees and removed an additional 0.177652 m3. Full-bucket hold, lift-out, uncuttable steep penetration, mobile-base cutting, truck reception and volume balance passed. Original guard fails independent-joint and deep-face-cut regressions; initial partial fix failed actual main-scene retraction, motivating measurement of the real oblique tooth trajectory.

Delivered BucketSoilResistance.cs, SoilResistanceTests.cs and BladeMainSceneTests.cs after checking main-file hashes for concurrent edits. Backup: /home/sk/Unity/arm-guard-backup-20260918. Files match validated isolated project byte for byte. No scene or user settings overwritten. Restart Play after scripts recompile. Screenshot posture was approximated through actual main-scene down-entry and retraction; exact user joint angles were not available. Back scraping still requires a bucket-angle change; floor and full-bucket holds remain intentional. No Web rebuild.

# Manual soil unloading

Approved scope: gravity-relative bucket opening, angle hysteresis and dwell,
gradual transfer into configured receivers, procedural received-soil display,
volume/mass ledger and coordinated reset. No particle rigidbodies or Web rebuild.

Implementation sequence:
1. Complete: receiver accounting and bucket-owned transfer API.
2. Complete: gravity/receiver gated unloading with conservative interruption behavior.
3. Complete: independent Demo 2 receiving platform, stream visual and operational status.
4. Complete: 92 EditMode and 6 PlayMode tests pass, including actual articulated
   digging and unloading; flow and completed-state screenshots inspected.

Volumes use excavated in-situ cubic metres (no bulking factor). Receiver visual
is a level accumulated layer, not a granular soil simulation. Receivers must be
upright with unit scale. Reset restores all buckets sharing the same terrain and
removes only that terrain's contributions from receivers.

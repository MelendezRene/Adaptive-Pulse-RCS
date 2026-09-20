# Changelog

## v0.3.0-b1 — Beta 1

First beta baseline.

### Added

- Per-`thrusterTransform` force model.
- Per-`thrusterTransform` torque model using current vessel center of mass.
- KSP `useZaxis` RCS thrust-direction handling.
- Filtering of inactive thruster transforms.
- Pitch / roll / yaw authority classification.
- Translation X / Y / Z authority classification.
- Automatic selection of RCS modules that can contribute to the requested control direction.
- Automatic pulse-length calculation from available authority and vessel mass.
- Rotational pulse estimate using RCS torque and approximate vessel inertia.
- Translation pulse estimate using RCS force and vessel mass.
- Dynamic geometry recalculation after significant vessel mass change.
- Continuous-fire fallback for high control demand.
- Input deadband and automatic pulse off-time.
- In-flight status window showing module count, transform count, selected modules and computed pulse duration.
- Stock RCS restoration control.

### Design decision

Beta 1 analyses nozzles individually but gates at `ModuleRCS` level. This avoids mutating the internal thruster-transform collection while KSP physics is running.

### Next targets

- Test and tune pulse model with RO spacecraft of different masses.
- Improve inertia estimation.
- Add measured-response feedback after each pulse.
- Improve mixed rotation + translation allocation.
- Investigate safe per-transform actuation.
- Package a compiled beta DLL after successful build verification.

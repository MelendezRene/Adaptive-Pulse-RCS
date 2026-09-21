# Changelog

## v0.3.5-b1.5 — Beta 1.5

### Added

- Per-axis pulse channels for pitch, roll, yaw, X, Z and Y translation.
- Per-thruster scoring inside the plugin to identify the best RCS nozzles for each requested axis.
- Coupling penalty so the allocator prefers thrusters that create the requested force/torque while minimizing unwanted motion on other axes.
- Improved per-axis inertia estimate based on vessel part masses and their position relative to the current center of mass.
- Precision / docking mode with shorter maximum pulses and longer settling time.
- New allocator telemetry showing thruster candidates, selected modules, per-axis force/torque authority and inertia estimates.
- Automatic geometry/inertia recalculation after smaller mass changes.

### Changed

- Module selection is now driven by per-thruster analysis rather than simple aggregate module authority.
- Pulse length is calculated independently for every rotational and translational axis.
- RCS still fires through stock ModuleRCS/ModuleRCSFX so RO/RealFuels resource consumption and effects remain under the normal KSP RCS system.

### Experimental design note

Beta 1.5 introduces an individual-thruster allocator for analysis and selection, but does not directly bypass ModuleRCS to apply custom forces. This is intentional: direct per-transform force injection would risk bypassing RealFuels resource consumption and RCS effects. The plugin therefore selects the best containing RCS modules while preserving the normal KSP/RO firing path.

### Compatibility

- KSP 1.12.x
- Realism Overhaul / RealFuels
- ModuleRCS / ModuleRCSFX
- Stock SAS
- MechJeb2 and other systems that command normal FlightCtrlState

## v0.3.4-b1.4 — Beta 1.4

### Added

- Stock KSP Application Launcher button to show/hide the Adaptive Pulse RCS window.
- In-game hotkey reassignment with persistent settings in `GameData/AdaptivePulseRCS/PluginData/Settings.cfg`.
- Separate adaptive pulse channels for rotation and translation.
- Real module-level RCS selection: modules that cannot contribute to the current command are temporarily disabled while the pulse controller is active.
- Separate telemetry for rotation-selected and translation-selected RCS modules.
- More explicit startup, vessel attach, scan, toolbar, geometry recalculation and settings logging.

### Changed

- Default hotkey remains `Ctrl+Shift+P`, but it can now be changed in flight.
- Rotation and translation no longer share one common pulse gate.
- Pulse sizing is tuned separately for translational delta-v and rotational rate change.
- Reaction-wheel suppression is only applied when rotational RCS authority is actually selected.
- Idle/off states restore each RCS module's original enabled state and thrust percentage.

### Compatibility

- KSP 1.12.x
- Realism Overhaul / RealFuels
- ModuleRCS / ModuleRCSFX
- Stock SAS
- MechJeb2 and other systems that command normal `FlightCtrlState`

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
- Input deadband and automatic pulse off-time.
- In-flight status window showing module count, transform count, selected modules and computed pulse duration.
- Stock RCS restoration control.

# Adaptive Pulse RCS

Universal automatic pulse controller for Kerbal Space Program 1.12.x.

## Beta 2.0 — v0.4.0-b2.0

Adaptive Pulse RCS makes spacecraft RCS behave more like an automatically managed attitude-control system. It reads the control request produced by the player, stock SAS, MechJeb or another normal FlightCtrlState source, analyzes the RCS authority available on the vessel and then schedules short pulses independently for each control axis.

## Beta 2.0 architecture

Beta 2.0 separates three responsibilities:

1. **Authority allocator / analyzer**
   - inspects every active RCS nozzle transform;
   - measures nozzle position relative to the current vessel center of mass;
   - calculates force and torque contribution;
   - scores useful nozzle candidates for pitch, roll, yaw and translation;
   - penalizes unwanted same-domain and cross-domain coupling.

2. **Per-axis pulse scheduler**
   - runs independent pulse channels for pitch, roll, yaw and X/Z/Y translation;
   - sizes pulses using available authority, vessel mass and estimated inertia;
   - provides a precision/docking mode with shorter pulses and longer settling time.

3. **Stock / RO firing path**
   - Adaptive Pulse RCS does **not** automatically change `ModuleRCS.rcsEnabled` or `thrustPercentage`;
   - KSP / RO / RealFuels remains responsible for physical RCS firing, propellant consumption and effects.

This is intentional. Beta 2.0 does not directly inject custom forces at individual thruster transforms, because doing so could bypass RealFuels resource handling or RCS effects. The allocator currently determines authority and pulse strategy; a future true per-nozzle actuator can be added only after a safe stock/RO-compatible firing path is proven.

## Compatibility target

- KSP 1.12.x
- Realism Overhaul
- RealFuels
- ModuleRCS / ModuleRCSFX
- MechJeb2
- stock SAS
- kOS or other systems generating normal flight-control inputs

Adaptive Pulse RCS does not replace guidance. Guidance decides *where the spacecraft should move*; Adaptive Pulse RCS decides *how the available RCS control request should be pulsed*.

## Controls

- Stock RCS action group: master hardware enable.
- `Ctrl + Shift + P`: default show/hide hotkey.
- **Change hotkey**: reassign the UI hotkey in flight.
- **Automatic adaptive pulse control**: enable/disable the controller.
- **RCS priority**: temporarily suppress reaction wheels while rotational RCS control is active.
- **Precision / docking mode**: shorter pulses and longer settling time.
- **Re-scan vessel**: rebuild the RCS authority model.
- **Enable all RCS modules**: manual recovery tool for craft whose RCS were left disabled by an older beta.
- **Restore stock RCS**: disables Adaptive Pulse RCS control without automatically rewriting per-part RCS enabled states.

## Beta 2.0 test priorities

Please test:

- whether all RCS modules can be enabled normally;
- manual translation and rotation;
- stock SAS attitude hold;
- MechJeb attitude control and translation commands;
- pulse smoothness on light and heavy RO spacecraft;
- precision/docking mode;
- behavior after staging and docking;
- whether any RCS module becomes unexpectedly disabled;
- unwanted translation during rotation or unwanted rotation during translation.

If you find a problem, a fresh `KSP.log` is especially useful.

## Source layout

- `src/AdaptivePulseRCS/` — C# source and project.
- `GameData/AdaptivePulseRCS/` — installable mod structure.

## License

MIT.

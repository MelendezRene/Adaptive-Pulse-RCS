# Adaptive Pulse RCS

Universal automatic pulse controller for Kerbal Space Program 1.12.x.

## Goal

Adaptive Pulse RCS sits **between the guidance demand and the RCS hardware behavior** without replacing SAS, MechJeb or kOS.

The controller automatically:

- detects `ModuleRCS` / `ModuleRCSFX`;
- reads the vessel mass and current center of mass;
- estimates available RCS force and rotational torque;
- measures control demand from pitch/yaw/roll and translation;
- calculates pulse duration automatically;
- shortens pulses as the vehicle approaches the requested state;
- allows longer/continuous firing when demand is large;
- keeps a deadband so RCS does not chatter continuously;
- re-scans after staging/docking/undocking or vessel changes.

It does **not** alter RealFuels propellant definitions. RO/RealFuels continues to determine propellant, Isp, pressure feed and actual thruster performance.

## Current development stage

`v0.2.0-dev`

The first implementation performs adaptive gating at **ModuleRCS module level**. A future version will add per-thruster-transform scheduling for parts containing several independently useful nozzles.

## Automatic control model

For rotation, the controller estimates:

`torque ~= thrust * lever arm`

and:

`angular acceleration ~= torque / vessel moment of inertia`

For translation:

`acceleration ~= available thrust / vessel mass`

The requested control magnitude, current angular rate and available authority determine the next pulse length automatically.

The player does not need to enter pulse duration.

## Compatibility target

- KSP 1.12.x
- Realism Overhaul
- RealFuels
- ModuleRCS / ModuleRCSFX
- MechJeb2
- stock SAS
- kOS-generated flight controls

## Build

KSP managed assemblies are **not committed to this repository**.

Copy these files into `lib/`:

- `Assembly-CSharp.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.IMGUIModule.dll`

Then run:

```powershell
./build.ps1
```

The DLL is copied to:

`GameData/AdaptivePulseRCS/Plugins/AdaptivePulseRCS.dll`

## Controls

- `Alt + P`: show/hide status window.
- `Adaptive Pulse RCS`: master enable.
- Stock RCS action group still acts as the main hardware enable.

All pulse timing is automatic.

## Design rule

The plugin must never silently change propellants or engine configs. It controls **when RCS fires**, while the installed spacecraft/RO configuration controls **what the RCS is**.

## License

MIT.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptivePulseRCS
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class AdaptivePulseRCSController : MonoBehaviour
    {
        private const float MinPulse = 0.02f;
        private const float MaxPulse = 0.75f;
        private const float MinOffTime = 0.04f;
        private const float InputDeadband = 0.025f;
        private const float ContinuousDemand = 0.88f;

        private Vessel vessel;
        private bool enabledController = true;
        private bool showWindow = true;
        private Rect window = new Rect(320, 120, 360, 250);

        private readonly List<ModuleState> modules = new List<ModuleState>();

        private float demandRotation;
        private float demandTranslation;
        private float nextPulse;
        private float pulseRemaining;
        private float offRemaining;
        private bool gateOn;

        private sealed class ModuleState
        {
            public ModuleRCS Module;
            public bool OriginalEnabled;
            public float EstimatedForce;
            public float EstimatedLeverArm;
        }

        public void Start()
        {
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onVesselWasModified.Add(OnVesselModified);
            Attach(FlightGlobals.ActiveVessel);
        }

        public void OnDestroy()
        {
            Restore();
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselWasModified.Remove(OnVesselModified);
            if (vessel != null)
                vessel.OnFlyByWire -= ReadControls;
        }

        private void OnVesselChange(Vessel v)
        {
            Attach(v);
        }

        private void OnVesselModified(Vessel v)
        {
            if (v == vessel)
                ScanModules();
        }

        private void Attach(Vessel v)
        {
            Restore();

            if (vessel != null)
                vessel.OnFlyByWire -= ReadControls;

            vessel = v;

            if (vessel != null)
            {
                vessel.OnFlyByWire += ReadControls;
                ScanModules();
            }
        }

        private void ScanModules()
        {
            Restore();
            modules.Clear();

            if (vessel == null || vessel.parts == null)
                return;

            Vector3d com = vessel.findWorldCenterOfMass();

            foreach (Part part in vessel.parts)
            {
                if (part == null)
                    continue;

                foreach (PartModule pm in part.Modules)
                {
                    ModuleRCS rcs = pm as ModuleRCS;
                    if (rcs == null)
                        continue;

                    ModuleState state = new ModuleState();
                    state.Module = rcs;
                    state.OriginalEnabled = rcs.rcsEnabled;

                    int count = 1;
                    try
                    {
                        if (rcs.thrusterTransforms != null && rcs.thrusterTransforms.Count > 0)
                            count = rcs.thrusterTransforms.Count;
                    }
                    catch { }

                    state.EstimatedForce = Mathf.Max(0.001f, rcs.thrusterPower) * count;

                    double lever = 1.0;
                    try
                    {
                        lever = (part.transform.position - (Vector3)com).magnitude;
                    }
                    catch { }

                    state.EstimatedLeverArm = Mathf.Max(0.10f, (float)lever);
                    modules.Add(state);
                }
            }
        }

        private void ReadControls(FlightCtrlState c)
        {
            if (!enabledController)
            {
                demandRotation = 0f;
                demandTranslation = 0f;
                return;
            }

            demandRotation = Mathf.Max(
                Mathf.Abs(c.pitch),
                Mathf.Abs(c.yaw),
                Mathf.Abs(c.roll)
            );

            demandTranslation = Mathf.Max(
                Mathf.Abs(c.X),
                Mathf.Abs(c.Y),
                Mathf.Abs(c.Z)
            );
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null)
                return;

            if (!enabledController || !vessel.ActionGroups[KSPActionGroup.RCS])
            {
                SetGate(false);
                pulseRemaining = 0f;
                offRemaining = 0f;
                return;
            }

            float demand = Mathf.Max(demandRotation, demandTranslation);

            if (demand <= InputDeadband)
            {
                SetGate(false);
                pulseRemaining = 0f;
                offRemaining = 0f;
                return;
            }

            if (demand >= ContinuousDemand)
            {
                SetGate(true);
                pulseRemaining = 0f;
                offRemaining = 0f;
                nextPulse = 0f;
                return;
            }

            float dt = TimeWarp.fixedDeltaTime;

            if (gateOn)
            {
                pulseRemaining -= dt;
                if (pulseRemaining <= 0f)
                {
                    SetGate(false);
                    offRemaining = MinOffTime;
                }
                return;
            }

            if (offRemaining > 0f)
            {
                offRemaining -= dt;
                return;
            }

            nextPulse = CalculatePulseLength();
            pulseRemaining = nextPulse;
            SetGate(true);
        }

        private float CalculatePulseLength()
        {
            float totalForce = 0f;
            float totalTorque = 0f;

            foreach (ModuleState s in modules)
            {
                if (!s.OriginalEnabled)
                    continue;

                totalForce += s.EstimatedForce;
                totalTorque += s.EstimatedForce * s.EstimatedLeverArm;
            }

            totalForce = Mathf.Max(0.001f, totalForce);
            totalTorque = Mathf.Max(0.001f, totalTorque);

            float mass = 1f;
            try
            {
                mass = Mathf.Max(0.01f, (float)vessel.GetTotalMass());
            }
            catch { }

            float linearAccel = totalForce / mass;

            float characteristicRadius = EstimateCharacteristicRadius();
            float inertiaEstimate = Mathf.Max(0.01f, mass * characteristicRadius * characteristicRadius);
            float angularAccel = totalTorque / inertiaEstimate;

            float translationPulse = demandTranslation > InputDeadband
                ? (0.12f * demandTranslation) / Mathf.Max(0.001f, linearAccel)
                : 0f;

            float currentAngularRate = 0f;
            try
            {
                currentAngularRate = (float)vessel.angularVelocity.magnitude;
            }
            catch { }

            float wantedAngularDelta = Mathf.Max(
                0.0025f,
                0.10f * demandRotation - 0.30f * currentAngularRate
            );

            float rotationPulse = demandRotation > InputDeadband
                ? wantedAngularDelta / Mathf.Max(0.001f, angularAccel)
                : 0f;

            float requested = Mathf.Max(rotationPulse, translationPulse);

            float demand = Mathf.Max(demandRotation, demandTranslation);
            requested *= Mathf.Lerp(0.35f, 1.0f, demand);

            return Mathf.Clamp(requested, MinPulse, MaxPulse);
        }

        private float EstimateCharacteristicRadius()
        {
            if (vessel == null || vessel.parts == null || vessel.parts.Count == 0)
                return 1f;

            Vector3d com = vessel.findWorldCenterOfMass();
            double sum = 0.0;
            int n = 0;

            foreach (Part p in vessel.parts)
            {
                if (p == null)
                    continue;

                sum += (p.transform.position - (Vector3)com).magnitude;
                n++;
            }

            if (n == 0)
                return 1f;

            return Mathf.Max(0.5f, (float)(sum / n));
        }

        private void SetGate(bool on)
        {
            gateOn = on;

            foreach (ModuleState state in modules)
            {
                if (state.Module != null)
                    state.Module.rcsEnabled = on && state.OriginalEnabled;
            }
        }

        private void Restore()
        {
            foreach (ModuleState state in modules)
            {
                if (state.Module != null)
                    state.Module.rcsEnabled = state.OriginalEnabled;
            }
        }

        public void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;

            if (Input.GetKeyDown(KeyCode.P) &&
                (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
            {
                showWindow = !showWindow;
            }
        }

        public void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !showWindow)
                return;

            window = GUILayout.Window(
                GetInstanceID(),
                window,
                DrawWindow,
                "Adaptive Pulse RCS"
            );
        }

        private void DrawWindow(int id)
        {
            bool before = enabledController;
            enabledController = GUILayout.Toggle(enabledController, "Automatic pulse control");

            if (before && !enabledController)
                Restore();
            else if (!before && enabledController)
                ScanModules();

            GUILayout.Label("RCS modules: " + modules.Count);
            GUILayout.Label("Rotation demand: " + demandRotation.ToString("F3"));
            GUILayout.Label("Translation demand: " + demandTranslation.ToString("F3"));
            GUILayout.Label("Next/last pulse: " + nextPulse.ToString("F3") + " s");
            GUILayout.Label(gateOn ? "RCS gate: ON" : "RCS gate: OFF");

            if (GUILayout.Button("Re-scan vessel"))
                ScanModules();

            if (GUILayout.Button("Restore stock control"))
            {
                enabledController = false;
                Restore();
            }

            GUILayout.Label("Alt+P: show/hide");
            GUI.DragWindow();
        }
    }
}

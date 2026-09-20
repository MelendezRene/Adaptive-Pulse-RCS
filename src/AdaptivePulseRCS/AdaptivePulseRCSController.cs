using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptivePulseRCS
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class AdaptivePulseRCSController : MonoBehaviour
    {
        private const float MinPulse = 0.02f;
        private const float MaxPulse = 0.80f;
        private const float MinOffTime = 0.04f;
        private const float InputDeadband = 0.025f;
        private const float ContinuousDemand = 0.90f;
        private const float AuthorityEpsilon = 0.0001f;

        private Vessel vessel;
        private bool enabledController = true;
        private bool showWindow = true;
        private Rect window = new Rect(320, 120, 390, 300);

        private readonly List<ModuleModel> modules = new List<ModuleModel>();

        private Vector3 rotationDemand;     // x=pitch, y=roll, z=yaw
        private Vector3 translationDemand;  // x=X, y=Z, z=Y (KSP part-space convention)

        private float nextPulse;
        private float pulseRemaining;
        private float offRemaining;
        private bool gateOn;
        private int selectedModuleCount;
        private int thrusterCount;
        private float selectedForce;
        private float selectedTorque;
        private float lastScanMass;

        private sealed class ModuleModel
        {
            public ModuleRCS Module;
            public bool OriginalEnabled;
            public float OriginalThrustPercentage;
            public readonly List<ThrusterModel> Thrusters = new List<ThrusterModel>();

            public Vector3 PositiveTorque;
            public Vector3 NegativeTorque;
            public Vector3 PositiveForce;
            public Vector3 NegativeForce;

            public bool Selected;
        }

        private sealed class ThrusterModel
        {
            public Transform Transform;
            public Vector3 ForceWorld;
            public Vector3 TorqueWorld;
            public Vector3 ForceLocal;
            public Vector3 TorqueLocal;
            public float ForceMagnitude;
        }

        public void Start()
        {
            Debug.Log("[AdaptivePulseRCS] Beta 1.1 starting - MechJeb compatibility gate active");
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onVesselWasModified.Add(OnVesselModified);
            GameEvents.onVesselCreate.Add(OnVesselCreate);
            Attach(FlightGlobals.ActiveVessel);
        }

        public void OnDestroy()
        {
            RestoreAll();

            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselWasModified.Remove(OnVesselModified);
            GameEvents.onVesselCreate.Remove(OnVesselCreate);

            if (vessel != null)
                vessel.OnFlyByWire -= ReadControls;
        }

        private void OnVesselChange(Vessel v)
        {
            Attach(v);
        }

        private void OnVesselCreate(Vessel v)
        {
            if (v == vessel)
                ScanModules();
        }

        private void OnVesselModified(Vessel v)
        {
            if (v == vessel)
                ScanModules();
        }

        private void Attach(Vessel v)
        {
            RestoreAll();

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
            RestoreAll();
            modules.Clear();
            thrusterCount = 0;

            if (vessel == null || vessel.parts == null || vessel.ReferenceTransform == null)
                return;

            Vector3 com = vessel.CoM;
            lastScanMass = SafeMass();
            Debug.Log("[AdaptivePulseRCS] Scanning vessel " + vessel.vesselName + " mass=" + lastScanMass.ToString("F3") + " t");

            foreach (Part part in vessel.parts)
            {
                if (part == null)
                    continue;

                foreach (PartModule pm in part.Modules)
                {
                    ModuleRCS rcs = pm as ModuleRCS;
                    if (rcs == null)
                        continue;

                    ModuleModel model = new ModuleModel();
                    model.Module = rcs;
                    model.OriginalEnabled = rcs.rcsEnabled;
                    model.OriginalThrustPercentage = rcs.thrustPercentage;

                    BuildThrusterModels(model, rcs, com);
                    AccumulateAuthority(model, rcs);

                    modules.Add(model);
                }
            }

            Debug.Log("[AdaptivePulseRCS] Scan complete modules=" + modules.Count + " thrusterTransforms=" + thrusterCount);
        }

        private void BuildThrusterModels(ModuleModel model, ModuleRCS rcs, Vector3d com)
        {
            if (rcs.thrusterTransforms == null)
                return;

            float powerFactor = Mathf.Max(0f, rcs.thrusterPower);

            try
            {
                powerFactor *= Mathf.Clamp01(model.OriginalThrustPercentage * 0.01f);
            }
            catch { }

            foreach (Transform t in rcs.thrusterTransforms)
            {
                if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy)
                    continue;

                Vector3 direction = rcs.useZaxis ? -t.forward : t.up;
                if (direction.sqrMagnitude < 0.000001f)
                    continue;

                direction.Normalize();

                Vector3 force = direction * powerFactor;
                Vector3 arm = t.position - (Vector3)com;
                Vector3 torque = Vector3.Cross(arm, force);

                ThrusterModel thruster = new ThrusterModel();
                thruster.Transform = t;
                thruster.ForceWorld = force;
                thruster.TorqueWorld = torque;
                thruster.ForceLocal = vessel.ReferenceTransform.InverseTransformDirection(force);
                thruster.TorqueLocal = vessel.ReferenceTransform.InverseTransformDirection(torque);
                thruster.ForceMagnitude = powerFactor;

                model.Thrusters.Add(thruster);
                thrusterCount++;
            }
        }

        private void AccumulateAuthority(ModuleModel model, ModuleRCS rcs)
        {
            Vector3 rotateEnable = new Vector3(
                rcs.enablePitch ? 1f : 0f,
                rcs.enableRoll ? 1f : 0f,
                rcs.enableYaw ? 1f : 0f
            );

            Vector3 translateEnable = new Vector3(
                rcs.enableX ? 1f : 0f,
                rcs.enableZ ? 1f : 0f,
                rcs.enableY ? 1f : 0f
            );

            foreach (ThrusterModel t in model.Thrusters)
            {
                Vector3 torque = Vector3.Scale(t.TorqueLocal, rotateEnable);
                Vector3 force = Vector3.Scale(t.ForceLocal, translateEnable);

                model.PositiveTorque += Positive(torque);
                model.NegativeTorque += Negative(torque);
                model.PositiveForce += Positive(force);
                model.NegativeForce += Negative(force);
            }
        }

        private static Vector3 Positive(Vector3 v)
        {
            return new Vector3(
                Mathf.Max(0f, v.x),
                Mathf.Max(0f, v.y),
                Mathf.Max(0f, v.z)
            );
        }

        private static Vector3 Negative(Vector3 v)
        {
            return new Vector3(
                Mathf.Min(0f, v.x),
                Mathf.Min(0f, v.y),
                Mathf.Min(0f, v.z)
            );
        }

        private void ReadControls(FlightCtrlState c)
        {
            if (!enabledController)
            {
                rotationDemand = Vector3.zero;
                translationDemand = Vector3.zero;
                return;
            }

            rotationDemand = new Vector3(c.pitch, c.roll, c.yaw);
            translationDemand = new Vector3(c.X, c.Z, c.Y);
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null)
                return;

            if (!enabledController || !vessel.ActionGroups[KSPActionGroup.RCS])
            {
                SetAllGates(false);
                ResetPulseState();
                return;
            }

            float currentMass = SafeMass();
            if (Mathf.Abs(currentMass - lastScanMass) > Mathf.Max(0.10f, lastScanMass * 0.03f))
                RecalculateGeometryOnly();

            float demand = Mathf.Max(MaxAbs(rotationDemand), MaxAbs(translationDemand));

            if (demand <= InputDeadband)
            {
                SetAllGates(false);
                ResetPulseState();
                return;
            }

            SelectUsefulModules();

            if (selectedModuleCount == 0)
            {
                SetAllGates(false);
                ResetPulseState();
                return;
            }

            if (demand >= ContinuousDemand)
            {
                ApplySelectionGate(true);
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
                    ApplySelectionGate(false);
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
            Debug.Log("[AdaptivePulseRCS] Pulse=" + nextPulse.ToString("F3") + "s selectedModules=" + selectedModuleCount + " force=" + selectedForce.ToString("F3") + " torque=" + selectedTorque.ToString("F3"));
            ApplySelectionGate(true);
        }

        private void RecalculateGeometryOnly()
        {
            if (vessel == null || vessel.ReferenceTransform == null)
                return;

            Vector3 com = vessel.CoM;
            lastScanMass = SafeMass();
            thrusterCount = 0;

            foreach (ModuleModel model in modules)
            {
                model.Thrusters.Clear();
                model.PositiveTorque = Vector3.zero;
                model.NegativeTorque = Vector3.zero;
                model.PositiveForce = Vector3.zero;
                model.NegativeForce = Vector3.zero;

                if (model.Module == null)
                    continue;

                BuildThrusterModels(model, model.Module, com);
                AccumulateAuthority(model, model.Module);
            }
        }

        private void SelectUsefulModules()
        {
            selectedModuleCount = 0;
            selectedForce = 0f;
            selectedTorque = 0f;

            foreach (ModuleModel model in modules)
            {
                model.Selected = false;

                if (model.Module == null || !model.OriginalEnabled)
                    continue;

                bool rotationUseful = AxisMatches(
                    rotationDemand,
                    model.PositiveTorque,
                    model.NegativeTorque
                );

                bool translationUseful = AxisMatches(
                    translationDemand,
                    model.PositiveForce,
                    model.NegativeForce
                );

                model.Selected = rotationUseful || translationUseful;

                if (!model.Selected)
                    continue;

                selectedModuleCount++;

                selectedTorque += RequestedAuthority(
                    rotationDemand,
                    model.PositiveTorque,
                    model.NegativeTorque
                );

                selectedForce += RequestedAuthority(
                    translationDemand,
                    model.PositiveForce,
                    model.NegativeForce
                );
            }

            selectedTorque = Mathf.Max(AuthorityEpsilon, selectedTorque);
            selectedForce = Mathf.Max(AuthorityEpsilon, selectedForce);
        }

        private static bool AxisMatches(Vector3 demand, Vector3 pos, Vector3 neg)
        {
            if (Mathf.Abs(demand.x) > InputDeadband &&
                ((demand.x > 0f && pos.x > AuthorityEpsilon) ||
                 (demand.x < 0f && neg.x < -AuthorityEpsilon)))
                return true;

            if (Mathf.Abs(demand.y) > InputDeadband &&
                ((demand.y > 0f && pos.y > AuthorityEpsilon) ||
                 (demand.y < 0f && neg.y < -AuthorityEpsilon)))
                return true;

            if (Mathf.Abs(demand.z) > InputDeadband &&
                ((demand.z > 0f && pos.z > AuthorityEpsilon) ||
                 (demand.z < 0f && neg.z < -AuthorityEpsilon)))
                return true;

            return false;
        }

        private static float RequestedAuthority(Vector3 demand, Vector3 pos, Vector3 neg)
        {
            float result = 0f;

            result += AxisAuthority(demand.x, pos.x, neg.x);
            result += AxisAuthority(demand.y, pos.y, neg.y);
            result += AxisAuthority(demand.z, pos.z, neg.z);

            return result;
        }

        private static float AxisAuthority(float demand, float positive, float negative)
        {
            if (Mathf.Abs(demand) <= InputDeadband)
                return 0f;

            return demand > 0f
                ? Mathf.Abs(positive) * Mathf.Abs(demand)
                : Mathf.Abs(negative) * Mathf.Abs(demand);
        }

        private float CalculatePulseLength()
        {
            float mass = SafeMass();
            float rotationMagnitude = MaxAbs(rotationDemand);
            float translationMagnitude = MaxAbs(translationDemand);

            float translationPulse = 0f;
            if (translationMagnitude > InputDeadband)
            {
                float acceleration = selectedForce / Mathf.Max(0.01f, mass);
                float desiredDeltaV = Mathf.Lerp(0.0025f, 0.15f, translationMagnitude);
                translationPulse = desiredDeltaV / Mathf.Max(0.0001f, acceleration);
            }

            float rotationPulse = 0f;
            if (rotationMagnitude > InputDeadband)
            {
                float radius = EstimateCharacteristicRadius();
                float inertiaEstimate = Mathf.Max(0.01f, mass * radius * radius);
                float angularAcceleration = selectedTorque / inertiaEstimate;

                float currentRate = 0f;
                try
                {
                    currentRate = (float)vessel.angularVelocity.magnitude;
                }
                catch { }

                float desiredRateChange = Mathf.Lerp(0.0015f, 0.12f, rotationMagnitude);

                if (currentRate > desiredRateChange)
                    desiredRateChange *= 0.35f;

                rotationPulse = desiredRateChange / Mathf.Max(0.0001f, angularAcceleration);
            }

            float requested = Mathf.Max(rotationPulse, translationPulse);
            float overallDemand = Mathf.Max(rotationMagnitude, translationMagnitude);

            requested *= Mathf.Lerp(0.30f, 1.0f, overallDemand);

            return Mathf.Clamp(requested, MinPulse, MaxPulse);
        }

        private float EstimateCharacteristicRadius()
        {
            if (vessel == null || vessel.parts == null || vessel.parts.Count == 0)
                return 1f;

            Vector3 com = vessel.CoM;
            double weightedDistance = 0.0;
            double totalMass = 0.0;

            foreach (Part p in vessel.parts)
            {
                if (p == null)
                    continue;

                double partMass = Math.Max(0.001, p.mass);
                double distance = (p.transform.position - (Vector3)com).magnitude;

                weightedDistance += distance * partMass;
                totalMass += partMass;
            }

            if (totalMass <= 0.0)
                return 1f;

            return Mathf.Max(0.5f, (float)(weightedDistance / totalMass));
        }

        private float SafeMass()
        {
            try
            {
                return Mathf.Max(0.01f, (float)vessel.GetTotalMass());
            }
            catch
            {
                return 1f;
            }
        }

        private void ApplySelectionGate(bool on)
        {
            gateOn = on;

            foreach (ModuleModel model in modules)
            {
                if (model.Module == null)
                    continue;

                // Never toggle rcsEnabled here. MechJeb and other guidance systems
                // may use that flag to determine whether RCS authority exists.
                model.Module.rcsEnabled = model.OriginalEnabled;

                bool shouldFire = on && model.Selected && model.OriginalEnabled;
                model.Module.thrustPercentage = shouldFire
                    ? model.OriginalThrustPercentage
                    : 0f;
            }
        }

        private void SetAllGates(bool on)
        {
            gateOn = on;

            foreach (ModuleModel model in modules)
            {
                if (model.Module == null)
                    continue;

                model.Module.rcsEnabled = model.OriginalEnabled;
                model.Module.thrustPercentage = on
                    ? model.OriginalThrustPercentage
                    : 0f;
            }

            if (!on)
            {
                selectedModuleCount = 0;
                selectedForce = 0f;
                selectedTorque = 0f;
            }
        }

        private void RestoreAll()
        {
            foreach (ModuleModel model in modules)
            {
                if (model.Module == null)
                    continue;

                model.Module.rcsEnabled = model.OriginalEnabled;
                model.Module.thrustPercentage = model.OriginalThrustPercentage;
            }

            gateOn = false;
            selectedModuleCount = 0;
            selectedForce = 0f;
            selectedTorque = 0f;
        }

        private void ResetPulseState()
        {
            pulseRemaining = 0f;
            offRemaining = 0f;
            nextPulse = 0f;
            gateOn = false;
        }

        private static float MaxAbs(Vector3 v)
        {
            return Mathf.Max(
                Mathf.Abs(v.x),
                Mathf.Abs(v.y),
                Mathf.Abs(v.z)
            );
        }

        public void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;

            if (Input.GetKeyDown(KeyCode.P) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
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
                "Adaptive Pulse RCS - Beta 1.1"
            );
        }

        private void DrawWindow(int id)
        {
            bool oldValue = enabledController;
            enabledController = GUILayout.Toggle(
                enabledController,
                "Automatic adaptive pulse control"
            );

            if (oldValue && !enabledController)
            {
                RestoreAll();
                ResetPulseState();
            }
            else if (!oldValue && enabledController)
            {
                ScanModules();
            }

            GUILayout.Label("RCS modules: " + modules.Count);
            GUILayout.Label("Thruster transforms: " + thrusterCount);
            GUILayout.Label("Selected modules: " + selectedModuleCount);

            GUILayout.Space(4);
            GUILayout.Label("Pitch/Roll/Yaw: " +
                rotationDemand.x.ToString("F2") + " / " +
                rotationDemand.y.ToString("F2") + " / " +
                rotationDemand.z.ToString("F2"));

            GUILayout.Label("X/Z/Y translation: " +
                translationDemand.x.ToString("F2") + " / " +
                translationDemand.y.ToString("F2") + " / " +
                translationDemand.z.ToString("F2"));

            GUILayout.Label("Adaptive pulse: " + nextPulse.ToString("F3") + " s");
            GUILayout.Label(gateOn ? "RCS gate: FIRING" : "RCS gate: idle");

            if (GUILayout.Button("Re-scan vessel"))
                ScanModules();

            if (GUILayout.Button("Restore stock RCS"))
            {
                enabledController = false;
                RestoreAll();
                ResetPulseState();
            }

            GUILayout.Label("Ctrl+Shift+P: show/hide");
            GUI.DragWindow();
        }
    }
}

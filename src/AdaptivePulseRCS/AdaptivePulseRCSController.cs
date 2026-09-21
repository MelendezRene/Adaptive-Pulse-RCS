using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using KSP.UI.Screens;

namespace AdaptivePulseRCS
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class AdaptivePulseRCSController : MonoBehaviour
    {
        private const float MinPulse = 0.02f;
        private const float MaxPulse = 0.35f;
        private const float MinOffTime = 0.10f;
        private const float InputDeadband = 0.025f;
        private const float AuthorityEpsilon = 0.0001f;
        private const float CouplingPenalty = 0.12f;

        private Vessel vessel;
        private bool enabledController = true;
        private bool showWindow = true;
        private Rect window = new Rect(320, 120, 450, 500);

        private readonly List<ModuleModel> modules = new List<ModuleModel>();
        private readonly List<WheelModel> reactionWheels = new List<WheelModel>();
        private bool rcsPriorityMode = true;
        private bool precisionMode = false;

        private Vector3 rotationDemand;
        private Vector3 translationDemand;
        private FlightCtrlState latestFinalState;
        private bool haveFinalState;

        private readonly PulseChannel pitchPulse = new PulseChannel();
        private readonly PulseChannel rollPulse = new PulseChannel();
        private readonly PulseChannel yawPulse = new PulseChannel();
        private readonly PulseChannel xPulse = new PulseChannel();
        private readonly PulseChannel zPulse = new PulseChannel();
        private readonly PulseChannel yPulse = new PulseChannel();

        private int selectedModuleCount;
        private int selectedThrusterCandidates;
        private int thrusterCount;
        private Vector3 selectedTorqueAuthority;
        private Vector3 selectedForceAuthority;
        private Vector3 inertiaEstimate = Vector3.one;
        private float lastScanMass;

        private ApplicationLauncherButton toolbarButton;
        private Texture2D toolbarIcon;

        private KeyCode hotkey = KeyCode.P;
        private bool hotkeyCtrl = true;
        private bool hotkeyShift = true;
        private bool hotkeyAlt = false;
        private bool waitingForHotkey;
        private int hotkeyCaptureStartFrame;

        private sealed class PulseChannel
        {
            public float LastPulse;
            public float Remaining;
            public float OffRemaining;
            public bool GateOn;

            public void Reset()
            {
                LastPulse = 0f;
                Remaining = 0f;
                OffRemaining = 0f;
                GateOn = false;
            }
        }

        private sealed class ModuleModel
        {
            public ModuleRCS Module;
            public bool OriginalEnabled;
            public float OriginalThrustPercentage;
            public readonly List<ThrusterModel> Thrusters = new List<ThrusterModel>();
            public bool Selected;
        }

        private sealed class WheelModel
        {
            public ModuleReactionWheel Module;
            public ModuleReactionWheel.WheelState OriginalState;
        }

        private sealed class ThrusterModel
        {
            public Transform Transform;
            public ModuleModel Parent;
            public Vector3 ForceLocal;
            public Vector3 TorqueLocal;
            public float ForceMagnitude;
        }

        public void Start()
        {
            Debug.Log("[AdaptivePulseRCS] Beta 1.7 starting - RO-safe per-axis adaptive allocator");
            LoadSettings();

            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onVesselWasModified.Add(OnVesselModified);
            GameEvents.onVesselCreate.Add(OnVesselCreate);
            GameEvents.onGUIApplicationLauncherReady.Add(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveToolbarButton);

            if (ApplicationLauncher.Ready)
                AddToolbarButton();

            Attach(FlightGlobals.ActiveVessel);
        }

        public void OnDestroy()
        {
            RestoreAll();

            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselWasModified.Remove(OnVesselModified);
            GameEvents.onVesselCreate.Remove(OnVesselCreate);
            GameEvents.onGUIApplicationLauncherReady.Remove(AddToolbarButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveToolbarButton);

            RemoveToolbarButton();

            if (vessel != null)
                vessel.OnPostAutopilotUpdate -= CaptureAutopilotControls;
                vessel.OnFlyByWire -= ProcessControls;

            if (toolbarIcon != null)
                Destroy(toolbarIcon);
        }

        private void AddToolbarButton()
        {
            if (toolbarButton != null || !ApplicationLauncher.Ready)
                return;

            toolbarIcon = BuildToolbarIcon();
            toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                () => showWindow = true,
                () => showWindow = false,
                null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT,
                toolbarIcon
            );

            Debug.Log("[AdaptivePulseRCS] Toolbar button ready");
        }

        private void RemoveToolbarButton()
        {
            if (toolbarButton == null || ApplicationLauncher.Instance == null)
                return;

            ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
            toolbarButton = null;
        }

        private static Texture2D BuildToolbarIcon()
        {
            Texture2D tex = new Texture2D(38, 38, TextureFormat.ARGB32, false);
            Color clear = new Color(0f, 0f, 0f, 0f);

            for (int y = 0; y < 38; y++)
                for (int x = 0; x < 38; x++)
                    tex.SetPixel(x, y, clear);

            for (int y = 16; y <= 21; y++)
                for (int x = 6; x <= 31; x++)
                    tex.SetPixel(x, y, Color.white);

            for (int x = 16; x <= 21; x++)
                for (int y = 6; y <= 31; y++)
                    tex.SetPixel(x, y, Color.white);

            tex.Apply(false, false);
            return tex;
        }

        private void OnVesselChange(Vessel v) { Attach(v); }
        private void OnVesselCreate(Vessel v) { if (v == vessel) ScanModules(); }
        private void OnVesselModified(Vessel v) { if (v == vessel) ScanModules(); }

        private void Attach(Vessel v)
        {
            RestoreAll();

            if (vessel != null)
                vessel.OnPostAutopilotUpdate -= CaptureAutopilotControls;
                vessel.OnFlyByWire -= ProcessControls;

            vessel = v;

            if (vessel != null)
            {
                vessel.OnPostAutopilotUpdate += CaptureAutopilotControls;
                vessel.OnFlyByWire += ProcessControls;
                Debug.Log("[AdaptivePulseRCS] Attached to vessel: " + vessel.vesselName);
                ScanModules();
            }
            else
            {
                Debug.Log("[AdaptivePulseRCS] No active vessel");
            }
        }

        private void ScanModules()
        {
            RestoreAll();
            modules.Clear();
            reactionWheels.Clear();
            thrusterCount = 0;

            if (vessel == null || vessel.parts == null || vessel.ReferenceTransform == null)
                return;

            Vector3 com = vessel.CoM;
            lastScanMass = SafeMass();

            foreach (Part part in vessel.parts)
            {
                if (part == null)
                    continue;

                foreach (PartModule pm in part.Modules)
                {
                    ModuleReactionWheel rw = pm as ModuleReactionWheel;
                    if (rw != null)
                    {
                        reactionWheels.Add(new WheelModel { Module = rw, OriginalState = rw.State });
                    }

                    ModuleRCS rcs = pm as ModuleRCS;
                    if (rcs == null)
                        continue;

                    ModuleModel model = new ModuleModel
                    {
                        Module = rcs,
                        OriginalEnabled = rcs.rcsEnabled,
                        OriginalThrustPercentage = rcs.thrustPercentage
                    };

                    BuildThrusterModels(model, rcs, com);
                    modules.Add(model);
                }
            }

            inertiaEstimate = EstimateInertiaTensor();

            Debug.Log("[AdaptivePulseRCS] Scan complete vessel=" + vessel.vesselName +
                      " mass=" + lastScanMass.ToString("F3") +
                      "t modules=" + modules.Count +
                      " thrusters=" + thrusterCount +
                      " inertia=" + FormatVector(inertiaEstimate));
        }

        private void BuildThrusterModels(ModuleModel model, ModuleRCS rcs, Vector3d com)
        {
            if (rcs.thrusterTransforms == null)
                return;

            float powerFactor = Mathf.Max(0f, rcs.thrusterPower);
            try { powerFactor *= Mathf.Clamp01(model.OriginalThrustPercentage * 0.01f); }
            catch { }

            foreach (Transform t in rcs.thrusterTransforms)
            {
                if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy)
                    continue;

                Vector3 direction = rcs.useZaxis ? -t.forward : t.up;
                if (direction.sqrMagnitude < 0.000001f)
                    continue;

                direction.Normalize();

                Vector3 forceWorld = direction * powerFactor;
                Vector3 arm = t.position - (Vector3)com;
                Vector3 torqueWorld = Vector3.Cross(arm, forceWorld);

                model.Thrusters.Add(new ThrusterModel
                {
                    Transform = t,
                    Parent = model,
                    ForceLocal = vessel.ReferenceTransform.InverseTransformDirection(forceWorld),
                    TorqueLocal = vessel.ReferenceTransform.InverseTransformDirection(torqueWorld),
                    ForceMagnitude = powerFactor
                });

                thrusterCount++;
            }
        }

        private void CaptureAutopilotControls(FlightCtrlState state)
        {
            if (state == null) return;
            latestFinalState = new FlightCtrlState();
            latestFinalState.CopyFrom(state);
            haveFinalState = true;
        }

        private void ProcessControls(FlightCtrlState state)
        {
            if (!enabledController || vessel == null || !vessel.ActionGroups[KSPActionGroup.RCS])
            {
                rotationDemand = Vector3.zero;
                translationDemand = Vector3.zero;
                RestoreModuleStates();
                RestoreReactionWheels();
                ResetPulseState();
                return;
            }

            // OnFlyByWire is where we gate the controls that KSP will actually apply.
            // MechJeb/SAS may write their commands during the autopilot pass, so retain
            // that final autopilot state and merge it with any direct/manual input.
            FlightCtrlState demandState = haveFinalState && latestFinalState != null ? latestFinalState : state;
            float pitchDemand = Mathf.Abs(demandState.pitch) >= Mathf.Abs(state.pitch) ? demandState.pitch : state.pitch;
            float rollDemand = Mathf.Abs(demandState.roll) >= Mathf.Abs(state.roll) ? demandState.roll : state.roll;
            float yawDemand = Mathf.Abs(demandState.yaw) >= Mathf.Abs(state.yaw) ? demandState.yaw : state.yaw;
            float xDemand = Mathf.Abs(demandState.X) >= Mathf.Abs(state.X) ? demandState.X : state.X;
            float zDemand = Mathf.Abs(demandState.Z) >= Mathf.Abs(state.Z) ? demandState.Z : state.Z;
            float yDemand = Mathf.Abs(demandState.Y) >= Mathf.Abs(state.Y) ? demandState.Y : state.Y;

            rotationDemand = new Vector3(pitchDemand, rollDemand, yawDemand);
            translationDemand = new Vector3(xDemand, zDemand, yDemand);

            float currentMass = SafeMass();
            if (Mathf.Abs(currentMass - lastScanMass) > Mathf.Max(0.10f, lastScanMass * 0.02f))
                RecalculateGeometryOnly();

            bool wantsRotation = MaxAbs(rotationDemand) > InputDeadband;
            bool wantsTranslation = MaxAbs(translationDemand) > InputDeadband;

            if (!wantsRotation && !wantsTranslation)
            {
                selectedModuleCount = 0;
                selectedThrusterCandidates = 0;
                selectedTorqueAuthority = Vector3.zero;
                selectedForceAuthority = Vector3.zero;
                RestoreModuleStates();
                RestoreReactionWheels();
                ResetPulseState();
                return;
            }

            SelectUsefulModules();
            ApplyModuleSelection();
            SetReactionWheelPriority(rcsPriorityMode && wantsRotation && selectedModuleCount > 0);

            if (selectedModuleCount == 0)
            {
                ResetPulseState();
                return;
            }

            UpdateAxisChannel(pitchPulse, rotationDemand.x, Axis(selectedTorqueAuthority, 0), Axis(inertiaEstimate, 0), true);
            UpdateAxisChannel(rollPulse, rotationDemand.y, Axis(selectedTorqueAuthority, 1), Axis(inertiaEstimate, 1), true);
            UpdateAxisChannel(yawPulse, rotationDemand.z, Axis(selectedTorqueAuthority, 2), Axis(inertiaEstimate, 2), true);

            float mass = SafeMass();
            UpdateAxisChannel(xPulse, translationDemand.x, Axis(selectedForceAuthority, 0), mass, false);
            UpdateAxisChannel(zPulse, translationDemand.y, Axis(selectedForceAuthority, 1), mass, false);
            UpdateAxisChannel(yPulse, translationDemand.z, Axis(selectedForceAuthority, 2), mass, false);

            if (Mathf.Abs(rotationDemand.x) > InputDeadband && !pitchPulse.GateOn) state.pitch = 0f;
            if (Mathf.Abs(rotationDemand.y) > InputDeadband && !rollPulse.GateOn) state.roll = 0f;
            if (Mathf.Abs(rotationDemand.z) > InputDeadband && !yawPulse.GateOn) state.yaw = 0f;

            if (Mathf.Abs(translationDemand.x) > InputDeadband && !xPulse.GateOn) state.X = 0f;
            if (Mathf.Abs(translationDemand.y) > InputDeadband && !zPulse.GateOn) state.Z = 0f;
            if (Mathf.Abs(translationDemand.z) > InputDeadband && !yPulse.GateOn) state.Y = 0f;
        }

        private void UpdateAxisChannel(PulseChannel channel, float demand, float authority, float inertiaOrMass, bool rotation)
        {
            bool active = Mathf.Abs(demand) > InputDeadband && authority > AuthorityEpsilon;
            if (!active)
            {
                channel.Reset();
                return;
            }

            float requested = rotation
                ? CalculateRotationPulseLength(Mathf.Abs(demand), authority, inertiaOrMass)
                : CalculateTranslationPulseLength(Mathf.Abs(demand), authority, inertiaOrMass);

            UpdatePulseChannel(channel, true, Mathf.Abs(demand), requested);
        }

        private void UpdatePulseChannel(PulseChannel channel, bool active, float demand, float requestedPulse)
        {
            if (!active)
            {
                channel.Reset();
                return;
            }

            float dt = TimeWarp.fixedDeltaTime;

            if (channel.GateOn)
            {
                channel.Remaining -= dt;
                if (channel.Remaining <= 0f)
                {
                    channel.GateOn = false;
                    channel.OffRemaining = CalculateOffTime(demand);
                }
                return;
            }

            if (channel.OffRemaining > 0f)
            {
                channel.OffRemaining -= dt;
                return;
            }

            channel.LastPulse = requestedPulse;
            channel.Remaining = requestedPulse;
            channel.GateOn = true;
        }

        private void SelectUsefulModules()
        {
            selectedModuleCount = 0;
            selectedThrusterCandidates = 0;
            selectedTorqueAuthority = Vector3.zero;
            selectedForceAuthority = Vector3.zero;

            foreach (ModuleModel model in modules)
                model.Selected = false;

            SelectForDemand(rotationDemand.x, true, 0);
            SelectForDemand(rotationDemand.y, true, 1);
            SelectForDemand(rotationDemand.z, true, 2);
            SelectForDemand(translationDemand.x, false, 0);
            SelectForDemand(translationDemand.y, false, 1);
            SelectForDemand(translationDemand.z, false, 2);

            foreach (ModuleModel model in modules)
            {
                if (model.Selected)
                    selectedModuleCount++;
            }
        }

        private void SelectForDemand(float demand, bool rotation, int axis)
        {
            if (Mathf.Abs(demand) <= InputDeadband)
                return;

            float sign = Mathf.Sign(demand);
            float bestScore = 0f;
            List<ThrusterModel> winners = new List<ThrusterModel>();

            foreach (ModuleModel model in modules)
            {
                if (model.Module == null || !model.Module.rcsEnabled)
                    continue;

                if (!AxisEnabled(model.Module, rotation, axis))
                    continue;

                foreach (ThrusterModel thruster in model.Thrusters)
                {
                    Vector3 authorityVector = rotation ? thruster.TorqueLocal : thruster.ForceLocal;
                    float primary = Axis(authorityVector, axis) * sign;
                    if (primary <= AuthorityEpsilon)
                        continue;

                    float coupling = OtherAxesMagnitude(authorityVector, axis);

                    float score = primary / (primary + coupling * CouplingPenalty + AuthorityEpsilon);
                    if (score <= AuthorityEpsilon)
                        continue;

                    if (score > bestScore)
                        bestScore = score;

                    winners.Add(thruster);
                }
            }

            if (winners.Count == 0)
                return;

            float threshold = bestScore * (precisionMode ? 0.55f : 0.30f);

            foreach (ThrusterModel thruster in winners)
            {
                Vector3 authorityVector = rotation ? thruster.TorqueLocal : thruster.ForceLocal;
                float primary = Axis(authorityVector, axis) * sign;
                float coupling = OtherAxesMagnitude(authorityVector, axis);
                float score = primary / (primary + coupling * CouplingPenalty + AuthorityEpsilon);

                if (score < threshold)
                    continue;

                thruster.Parent.Selected = true;
                selectedThrusterCandidates++;

                if (rotation)
                    AddAxisAbs(ref selectedTorqueAuthority, axis, primary);
                else
                    AddAxisAbs(ref selectedForceAuthority, axis, primary);
            }
        }

        private static bool AxisEnabled(ModuleRCS rcs, bool rotation, int axis)
        {
            if (rotation)
            {
                if (axis == 0) return rcs.enablePitch;
                if (axis == 1) return rcs.enableRoll;
                return rcs.enableYaw;
            }

            if (axis == 0) return rcs.enableX;
            if (axis == 1) return rcs.enableZ;
            return rcs.enableY;
        }

        private static float OtherAxesMagnitude(Vector3 v, int axis)
        {
            if (axis == 0) return Mathf.Abs(v.y) + Mathf.Abs(v.z);
            if (axis == 1) return Mathf.Abs(v.x) + Mathf.Abs(v.z);
            return Mathf.Abs(v.x) + Mathf.Abs(v.y);
        }

        private static void AddAxisAbs(ref Vector3 v, int axis, float amount)
        {
            amount = Mathf.Abs(amount);
            if (axis == 0) v.x += amount;
            else if (axis == 1) v.y += amount;
            else v.z += amount;
        }

        private void ApplyModuleSelection()
        {
            // Beta 1.7: do not write ModuleRCS.rcsEnabled or thrustPercentage here.
            // Those are persistent/player-owned settings (and may also be managed by RO).
            // Selection is internal; pulse gating is applied to FlightCtrlState instead.
        }

        private void RestoreModuleStates()
        {
            // Beta 1.7: no per-part RCS state restoration is required because the
            // controller no longer mutates ModuleRCS.rcsEnabled/thrustPercentage.
        }

        private void SetReactionWheelPriority(bool suppress)
        {
            foreach (WheelModel wheel in reactionWheels)
            {
                if (wheel.Module == null)
                    continue;

                wheel.Module.State = suppress
                    ? ModuleReactionWheel.WheelState.Disabled
                    : wheel.OriginalState;
            }
        }

        private void RestoreReactionWheels() { SetReactionWheelPriority(false); }

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
                if (model.Module != null)
                    BuildThrusterModels(model, model.Module, com);
            }

            inertiaEstimate = EstimateInertiaTensor();
            Debug.Log("[AdaptivePulseRCS] Geometry recalculated mass=" + lastScanMass.ToString("F3") +
                      " inertia=" + FormatVector(inertiaEstimate));
        }

        private Vector3 EstimateInertiaTensor()
        {
            if (vessel == null || vessel.parts == null || vessel.ReferenceTransform == null)
                return Vector3.one;

            Vector3 com = vessel.CoM;
            double ix = 0.0, iy = 0.0, iz = 0.0;

            foreach (Part p in vessel.parts)
            {
                if (p == null)
                    continue;

                double m = Math.Max(0.001, p.mass);
                Vector3 armWorld = p.transform.position - com;
                Vector3 r = vessel.ReferenceTransform.InverseTransformDirection(armWorld);

                ix += m * (r.y * r.y + r.z * r.z);
                iy += m * (r.x * r.x + r.z * r.z);
                iz += m * (r.x * r.x + r.y * r.y);
            }

            return new Vector3(
                Mathf.Max(0.01f, (float)ix),
                Mathf.Max(0.01f, (float)iy),
                Mathf.Max(0.01f, (float)iz)
            );
        }

        private float CalculateTranslationPulseLength(float demand, float authority, float mass)
        {
            float acceleration = authority / Mathf.Max(0.01f, mass);
            float maxDv = precisionMode ? 0.012f : 0.050f;
            float minDv = precisionMode ? 0.0005f : 0.0010f;
            float desiredDeltaV = Mathf.Lerp(minDv, maxDv, demand);
            float requested = desiredDeltaV / Mathf.Max(0.0001f, acceleration);
            requested *= Mathf.Lerp(0.35f, 1.0f, demand);
            return Mathf.Clamp(requested, MinPulse, precisionMode ? 0.18f : MaxPulse);
        }

        private float CalculateRotationPulseLength(float demand, float authority, float inertia)
        {
            float angularAcceleration = authority / Mathf.Max(0.01f, inertia);
            float minRate = precisionMode ? 0.0004f : 0.0008f;
            float maxRate = precisionMode ? 0.012f : 0.040f;
            float desiredRateChange = Mathf.Lerp(minRate, maxRate, demand);
            float requested = desiredRateChange / Mathf.Max(0.0001f, angularAcceleration);
            requested *= Mathf.Lerp(0.35f, 1.0f, demand);
            return Mathf.Clamp(requested, MinPulse, precisionMode ? 0.18f : MaxPulse);
        }

        private float CalculateOffTime(float demand)
        {
            float baseOff = precisionMode ? 0.32f : 0.20f;
            float minimum = precisionMode ? 0.16f : MinOffTime;
            return Mathf.Lerp(baseOff, minimum, Mathf.Clamp01(demand));
        }

        private float SafeMass()
        {
            try { return vessel == null ? 1f : Mathf.Max(0.01f, (float)vessel.GetTotalMass()); }
            catch { return 1f; }
        }

        private void RestoreAll()
        {
            RestoreModuleStates();
            RestoreReactionWheels();
            ResetPulseState();
            selectedModuleCount = 0;
            selectedThrusterCandidates = 0;
            selectedTorqueAuthority = Vector3.zero;
            selectedForceAuthority = Vector3.zero;
        }

        private void ResetPulseState()
        {
            pitchPulse.Reset();
            rollPulse.Reset();
            yawPulse.Reset();
            xPulse.Reset();
            zPulse.Reset();
            yPulse.Reset();
        }

        private static float MaxAbs(Vector3 v)
        {
            return Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }

        private static float Axis(Vector3 v, int axis)
        {
            return axis == 0 ? v.x : (axis == 1 ? v.y : v.z);
        }

        private static string FormatVector(Vector3 v)
        {
            return v.x.ToString("F2") + "/" + v.y.ToString("F2") + "/" + v.z.ToString("F2");
        }

        public void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;

            if (waitingForHotkey)
            {
                CaptureHotkey();
                return;
            }

            if (HotkeyPressed())
                showWindow = !showWindow;
        }

        private bool HotkeyPressed()
        {
            if (!Input.GetKeyDown(hotkey))
                return false;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

            return ctrl == hotkeyCtrl && shift == hotkeyShift && alt == hotkeyAlt;
        }

        private void BeginHotkeyCapture()
        {
            waitingForHotkey = true;
            hotkeyCaptureStartFrame = Time.frameCount;
        }

        private void CaptureHotkey()
        {
            if (Time.frameCount <= hotkeyCaptureStartFrame || !Input.anyKeyDown)
                return;

            Array values = Enum.GetValues(typeof(KeyCode));
            foreach (KeyCode code in values)
            {
                if (!Input.GetKeyDown(code) || IsModifierKey(code) || IsMouseKey(code))
                    continue;

                hotkey = code;
                hotkeyCtrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                hotkeyShift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                hotkeyAlt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                waitingForHotkey = false;
                SaveSettings();
                Debug.Log("[AdaptivePulseRCS] Hotkey changed to " + HotkeyLabel());
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
                waitingForHotkey = false;
        }

        private static bool IsModifierKey(KeyCode code)
        {
            return code == KeyCode.LeftControl || code == KeyCode.RightControl ||
                   code == KeyCode.LeftShift || code == KeyCode.RightShift ||
                   code == KeyCode.LeftAlt || code == KeyCode.RightAlt ||
                   code == KeyCode.LeftCommand || code == KeyCode.RightCommand;
        }

        private static bool IsMouseKey(KeyCode code)
        {
            return code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6;
        }

        private string HotkeyLabel()
        {
            string result = "";
            if (hotkeyCtrl) result += "Ctrl+";
            if (hotkeyShift) result += "Shift+";
            if (hotkeyAlt) result += "Alt+";
            return result + hotkey;
        }

        private string SettingsPath
        {
            get
            {
                return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "AdaptivePulseRCS", "PluginData", "Settings.cfg");
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return;

                ConfigNode n = ConfigNode.Load(SettingsPath);
                if (n == null)
                    return;

                string key = n.GetValue("key");
                KeyCode parsed;
                if (!string.IsNullOrEmpty(key) && Enum.TryParse(key, out parsed))
                    hotkey = parsed;

                bool parsedBool;
                if (bool.TryParse(n.GetValue("ctrl"), out parsedBool)) hotkeyCtrl = parsedBool;
                if (bool.TryParse(n.GetValue("shift"), out parsedBool)) hotkeyShift = parsedBool;
                if (bool.TryParse(n.GetValue("alt"), out parsedBool)) hotkeyAlt = parsedBool;
                if (bool.TryParse(n.GetValue("precisionMode"), out parsedBool)) precisionMode = parsedBool;

                Debug.Log("[AdaptivePulseRCS] Settings loaded hotkey=" + HotkeyLabel() +
                          " precisionMode=" + precisionMode);
            }
            catch (Exception ex)
            {
                Debug.LogError("[AdaptivePulseRCS] Settings load failed: " + ex);
            }
        }

        private void SaveSettings()
        {
            try
            {
                string directory = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                ConfigNode n = new ConfigNode("ADAPTIVE_PULSE_RCS_SETTINGS");
                n.AddValue("key", hotkey.ToString());
                n.AddValue("ctrl", hotkeyCtrl);
                n.AddValue("shift", hotkeyShift);
                n.AddValue("alt", hotkeyAlt);
                n.AddValue("precisionMode", precisionMode);
                n.Save(SettingsPath);
            }
            catch (Exception ex)
            {
                Debug.LogError("[AdaptivePulseRCS] Settings save failed: " + ex);
            }
        }

        public void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !showWindow)
                return;

            window = GUILayout.Window(GetInstanceID(), window, DrawWindow, "Adaptive Pulse RCS - Beta 1.7");
        }

        private void DrawWindow(int id)
        {
            bool oldValue = enabledController;
            enabledController = GUILayout.Toggle(enabledController, "Automatic adaptive pulse control");

            if (oldValue && !enabledController) RestoreAll();
            else if (!oldValue && enabledController) ScanModules();

            rcsPriorityMode = GUILayout.Toggle(rcsPriorityMode, "RCS priority (suppress reaction wheels)");

            bool oldPrecision = precisionMode;
            precisionMode = GUILayout.Toggle(precisionMode, "Precision / docking mode");
            if (oldPrecision != precisionMode)
                SaveSettings();

            GUILayout.Label("Vessel: " + (vessel != null ? vessel.vesselName : "NONE"));
            GUILayout.Label("RCS modules: " + modules.Count + " | Thrusters: " + thrusterCount);
            GUILayout.Label("Allocator: " + selectedThrusterCandidates + " thruster candidates -> " +
                            selectedModuleCount + " active modules");

            GUILayout.Label("Torque P/R/Y: " + FormatVector(selectedTorqueAuthority));
            GUILayout.Label("Force X/Z/Y: " + FormatVector(selectedForceAuthority));
            GUILayout.Label("Inertia P/R/Y: " + FormatVector(inertiaEstimate));

            GUILayout.Space(4);
            GUILayout.Label("ROT pulses P/R/Y: " +
                            pitchPulse.LastPulse.ToString("F3") + " / " +
                            rollPulse.LastPulse.ToString("F3") + " / " +
                            yawPulse.LastPulse.ToString("F3") + " s");
            GUILayout.Label("TRANS pulses X/Z/Y: " +
                            xPulse.LastPulse.ToString("F3") + " / " +
                            zPulse.LastPulse.ToString("F3") + " / " +
                            yPulse.LastPulse.ToString("F3") + " s");

            GUILayout.Space(4);
            GUILayout.Label("Window hotkey: " + HotkeyLabel());

            if (waitingForHotkey)
                GUILayout.Label("Press the new key combination (Esc cancels)");
            else if (GUILayout.Button("Change hotkey"))
                BeginHotkeyCapture();

            if (GUILayout.Button("Re-scan vessel"))
                ScanModules();

            if (GUILayout.Button("Restore stock RCS"))
            {
                enabledController = false;
                RestoreAll();
            }

            GUI.DragWindow();
        }
    }
}

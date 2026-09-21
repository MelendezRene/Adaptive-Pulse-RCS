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

        private Vessel vessel;
        private bool enabledController = true;
        private bool showWindow = true;
        private Rect window = new Rect(320, 120, 420, 390);

        private readonly List<ModuleModel> modules = new List<ModuleModel>();
        private readonly List<WheelModel> reactionWheels = new List<WheelModel>();
        private bool rcsPriorityMode = true;

        private Vector3 rotationDemand;
        private Vector3 translationDemand;

        private readonly PulseChannel rotationPulse = new PulseChannel();
        private readonly PulseChannel translationPulse = new PulseChannel();

        private int selectedModuleCount;
        private int selectedRotationModules;
        private int selectedTranslationModules;
        private int thrusterCount;
        private float selectedForce;
        private float selectedTorque;
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

            public Vector3 PositiveTorque;
            public Vector3 NegativeTorque;
            public Vector3 PositiveForce;
            public Vector3 NegativeForce;

            public bool RotationSelected;
            public bool TranslationSelected;
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
            public Vector3 ForceWorld;
            public Vector3 TorqueWorld;
            public Vector3 ForceLocal;
            public Vector3 TorqueLocal;
            public float ForceMagnitude;
        }

        public void Start()
        {
            Debug.Log("[AdaptivePulseRCS] Beta 1.4 starting");
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
                vessel.OnPostAutopilotUpdate -= ProcessControls;

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
                null,
                null,
                null,
                null,
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
            Color white = Color.white;

            for (int y = 0; y < 38; y++)
                for (int x = 0; x < 38; x++)
                    tex.SetPixel(x, y, clear);

            for (int y = 16; y <= 21; y++)
                for (int x = 6; x <= 31; x++)
                    tex.SetPixel(x, y, white);

            for (int x = 16; x <= 21; x++)
                for (int y = 6; y <= 31; y++)
                    tex.SetPixel(x, y, white);

            tex.Apply(false, false);
            return tex;
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
                vessel.OnPostAutopilotUpdate -= ProcessControls;

            vessel = v;

            if (vessel != null)
            {
                vessel.OnPostAutopilotUpdate += ProcessControls;
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
                        reactionWheels.Add(new WheelModel
                        {
                            Module = rw,
                            OriginalState = rw.State
                        });
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
                    AccumulateAuthority(model, rcs);
                    modules.Add(model);
                }
            }

            Debug.Log("[AdaptivePulseRCS] Scan complete vessel=" + vessel.vesselName +
                      " mass=" + lastScanMass.ToString("F3") +
                      "t modules=" + modules.Count +
                      " thrusters=" + thrusterCount +
                      " wheels=" + reactionWheels.Count);
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

                model.Thrusters.Add(new ThrusterModel
                {
                    Transform = t,
                    ForceWorld = force,
                    TorqueWorld = torque,
                    ForceLocal = vessel.ReferenceTransform.InverseTransformDirection(force),
                    TorqueLocal = vessel.ReferenceTransform.InverseTransformDirection(torque),
                    ForceMagnitude = powerFactor
                });

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
            return new Vector3(Mathf.Max(0f, v.x), Mathf.Max(0f, v.y), Mathf.Max(0f, v.z));
        }

        private static Vector3 Negative(Vector3 v)
        {
            return new Vector3(Mathf.Min(0f, v.x), Mathf.Min(0f, v.y), Mathf.Min(0f, v.z));
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

            rotationDemand = new Vector3(state.pitch, state.roll, state.yaw);
            translationDemand = new Vector3(state.X, state.Z, state.Y);

            float currentMass = SafeMass();
            if (Mathf.Abs(currentMass - lastScanMass) > Mathf.Max(0.10f, lastScanMass * 0.03f))
                RecalculateGeometryOnly();

            float rotationMagnitude = MaxAbs(rotationDemand);
            float translationMagnitude = MaxAbs(translationDemand);
            bool wantsRotation = rotationMagnitude > InputDeadband;
            bool wantsTranslation = translationMagnitude > InputDeadband;

            if (!wantsRotation && !wantsTranslation)
            {
                selectedModuleCount = 0;
                selectedRotationModules = 0;
                selectedTranslationModules = 0;
                selectedForce = 0f;
                selectedTorque = 0f;
                RestoreModuleStates();
                RestoreReactionWheels();
                ResetPulseState();
                return;
            }

            SelectUsefulModules();
            ApplyModuleSelection();

            bool hasUsefulRcs = selectedModuleCount > 0;
            SetReactionWheelPriority(rcsPriorityMode && wantsRotation && selectedRotationModules > 0);

            if (!hasUsefulRcs)
            {
                ResetPulseState();
                return;
            }

            float dt = TimeWarp.fixedDeltaTime;

            UpdatePulseChannel(
                rotationPulse,
                wantsRotation && selectedRotationModules > 0,
                rotationMagnitude,
                CalculateRotationPulseLength()
            );

            UpdatePulseChannel(
                translationPulse,
                wantsTranslation && selectedTranslationModules > 0,
                translationMagnitude,
                CalculateTranslationPulseLength()
            );

            if (wantsRotation && !rotationPulse.GateOn)
            {
                state.pitch = 0f;
                state.roll = 0f;
                state.yaw = 0f;
            }

            if (wantsTranslation && !translationPulse.GateOn)
            {
                state.X = 0f;
                state.Y = 0f;
                state.Z = 0f;
            }
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

        private void RestoreReactionWheels()
        {
            SetReactionWheelPriority(false);
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

            Debug.Log("[AdaptivePulseRCS] Geometry recalculated mass=" + lastScanMass.ToString("F3") + "t");
        }

        private void SelectUsefulModules()
        {
            selectedModuleCount = 0;
            selectedRotationModules = 0;
            selectedTranslationModules = 0;
            selectedForce = 0f;
            selectedTorque = 0f;

            foreach (ModuleModel model in modules)
            {
                model.RotationSelected = false;
                model.TranslationSelected = false;
                model.Selected = false;

                if (model.Module == null || !model.OriginalEnabled)
                    continue;

                model.RotationSelected = AxisMatches(rotationDemand, model.PositiveTorque, model.NegativeTorque);
                model.TranslationSelected = AxisMatches(translationDemand, model.PositiveForce, model.NegativeForce);
                model.Selected = model.RotationSelected || model.TranslationSelected;

                if (!model.Selected)
                    continue;

                selectedModuleCount++;

                if (model.RotationSelected)
                {
                    selectedRotationModules++;
                    selectedTorque += RequestedAuthority(rotationDemand, model.PositiveTorque, model.NegativeTorque);
                }

                if (model.TranslationSelected)
                {
                    selectedTranslationModules++;
                    selectedForce += RequestedAuthority(translationDemand, model.PositiveForce, model.NegativeForce);
                }
            }
        }

        private void ApplyModuleSelection()
        {
            foreach (ModuleModel model in modules)
            {
                if (model.Module == null)
                    continue;

                model.Module.rcsEnabled = model.OriginalEnabled && model.Selected;
                model.Module.thrustPercentage = model.OriginalThrustPercentage;
            }
        }

        private void RestoreModuleStates()
        {
            foreach (ModuleModel model in modules)
            {
                if (model.Module == null)
                    continue;

                model.Module.rcsEnabled = model.OriginalEnabled;
                model.Module.thrustPercentage = model.OriginalThrustPercentage;
            }
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
            return AxisAuthority(demand.x, pos.x, neg.x) +
                   AxisAuthority(demand.y, pos.y, neg.y) +
                   AxisAuthority(demand.z, pos.z, neg.z);
        }

        private static float AxisAuthority(float demand, float positive, float negative)
        {
            if (Mathf.Abs(demand) <= InputDeadband)
                return 0f;

            return demand > 0f
                ? Mathf.Abs(positive) * Mathf.Abs(demand)
                : Mathf.Abs(negative) * Mathf.Abs(demand);
        }

        private float CalculateTranslationPulseLength()
        {
            float magnitude = MaxAbs(translationDemand);
            if (magnitude <= InputDeadband || selectedForce <= AuthorityEpsilon)
                return MinPulse;

            float mass = SafeMass();
            float acceleration = selectedForce / Mathf.Max(0.01f, mass);
            float desiredDeltaV = Mathf.Lerp(0.0010f, 0.050f, magnitude);
            float requested = desiredDeltaV / Mathf.Max(0.0001f, acceleration);
            requested *= Mathf.Lerp(0.35f, 1.0f, magnitude);

            return Mathf.Clamp(requested, MinPulse, MaxPulse);
        }

        private float CalculateRotationPulseLength()
        {
            float magnitude = MaxAbs(rotationDemand);
            if (magnitude <= InputDeadband || selectedTorque <= AuthorityEpsilon)
                return MinPulse;

            float mass = SafeMass();
            float radius = EstimateCharacteristicRadius();
            float inertiaEstimate = Mathf.Max(0.01f, mass * radius * radius);
            float angularAcceleration = selectedTorque / inertiaEstimate;

            float currentRate = 0f;
            try
            {
                currentRate = (float)vessel.angularVelocity.magnitude;
            }
            catch { }

            float desiredRateChange = Mathf.Lerp(0.0008f, 0.040f, magnitude);
            if (currentRate > desiredRateChange)
                desiredRateChange *= 0.30f;

            float requested = desiredRateChange / Mathf.Max(0.0001f, angularAcceleration);
            requested *= Mathf.Lerp(0.35f, 1.0f, magnitude);

            return Mathf.Clamp(requested, MinPulse, MaxPulse);
        }

        private float CalculateOffTime(float demand)
        {
            return Mathf.Lerp(0.20f, MinOffTime, Mathf.Clamp01(demand));
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
                return vessel == null ? 1f : Mathf.Max(0.01f, (float)vessel.GetTotalMass());
            }
            catch
            {
                return 1f;
            }
        }

        private void RestoreAll()
        {
            RestoreModuleStates();
            RestoreReactionWheels();
            ResetPulseState();
            selectedModuleCount = 0;
            selectedRotationModules = 0;
            selectedTranslationModules = 0;
            selectedForce = 0f;
            selectedTorque = 0f;
        }

        private void ResetPulseState()
        {
            rotationPulse.Reset();
            translationPulse.Reset();
        }

        private static float MaxAbs(Vector3 v)
        {
            return Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
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
                return Path.Combine(
                    KSPUtil.ApplicationRootPath,
                    "GameData",
                    "AdaptivePulseRCS",
                    "PluginData",
                    "Settings.cfg"
                );
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

                Debug.Log("[AdaptivePulseRCS] Settings loaded hotkey=" + HotkeyLabel());
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

            window = GUILayout.Window(
                GetInstanceID(),
                window,
                DrawWindow,
                "Adaptive Pulse RCS - Beta 1.4"
            );
        }

        private void DrawWindow(int id)
        {
            bool oldValue = enabledController;
            enabledController = GUILayout.Toggle(enabledController, "Automatic adaptive pulse control");

            if (oldValue && !enabledController)
            {
                RestoreAll();
            }
            else if (!oldValue && enabledController)
            {
                ScanModules();
            }

            rcsPriorityMode = GUILayout.Toggle(rcsPriorityMode, "RCS priority (suppress reaction wheels)");

            GUILayout.Label("Vessel: " + (vessel != null ? vessel.vesselName : "NONE"));
            GUILayout.Label("RCS modules: " + modules.Count + " | Thrusters: " + thrusterCount);
            GUILayout.Label("Selected: " + selectedModuleCount +
                            " (ROT " + selectedRotationModules +
                            " / TRANS " + selectedTranslationModules + ")");

            GUILayout.Space(4);
            GUILayout.Label("Pitch/Roll/Yaw: " +
                rotationDemand.x.ToString("F2") + " / " +
                rotationDemand.y.ToString("F2") + " / " +
                rotationDemand.z.ToString("F2"));

            GUILayout.Label("X/Z/Y translation: " +
                translationDemand.x.ToString("F2") + " / " +
                translationDemand.y.ToString("F2") + " / " +
                translationDemand.z.ToString("F2"));

            GUILayout.Label("ROT pulse: " + rotationPulse.LastPulse.ToString("F3") +
                            " s - " + (rotationPulse.GateOn ? "FIRING" : "idle"));
            GUILayout.Label("TRANS pulse: " + translationPulse.LastPulse.ToString("F3") +
                            " s - " + (translationPulse.GateOn ? "FIRING" : "idle"));

            GUILayout.Space(4);
            GUILayout.Label("Window hotkey: " + HotkeyLabel());

            if (waitingForHotkey)
            {
                GUILayout.Label("Press the new key combination (Esc cancels)");
            }
            else if (GUILayout.Button("Change hotkey"))
            {
                BeginHotkeyCapture();
            }

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

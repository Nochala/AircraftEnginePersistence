using System;
using System.Collections.Generic;
using System.IO;
using GTA;
using GTA.Native;

namespace EngineStateManager
{
    public sealed class AircraftEnginePersistence : Script
    {
        private sealed class TrackedAircraft
        {
            public int Handle;
            public VehicleClass ClassType;

            // Cached classification (computed via bone scan)
            public bool IsPropPlane;

            // Prop-plane workaround: invisible 'ghost pilot' to prevent Rockstar empty-seat prop stall
            public int GhostPilotPedHandle;
            public bool GhostPilotActive;

            // "Sacred" guard: only true if we observed engine ON while player was seated.
            public bool ArmedForPersistence;
            public bool EngineWasOnWhileSeated;

            // State machine
            public bool HasExited;
            public bool WasPlayerInsideLastUpdate;

            // Timing
            public int NextReassertTime;
            public int ExitGraceUntilTime;       // post-exit enforcement window
            public int ExitPreemptUntilTime;     // while still in exit animation / transition
            public int EnterPreemptUntilTime;    // while entering animation / transition

            // Prevent respawning ghost pilot during player enter animation / nearby enter attempts
            public int GhostSuppressUntilTime;
            public int ExitDetectedTime;

            // RPM smoothing / dip suppression
            public float LastKnownRpm;

            // LRU pruning
            public int LastTouchedTime;
        }

        // ===== INI / SETTINGS =====
        private int _maxTrackedAircraft = 20;
        private int _maintenanceIntervalMs = 250;      // pruning/maintenance cadence
        private int _reassertPerAircraftMs = 900;      // baseline reassert interval (outside grace)
        private float _maxDistanceToTrack = -1f;       // <0 disables

        private bool _onlyReassertWhenEngineOff = true;
        private bool _forceHeliBladesFullSpeedAfterExit = true;

        private bool _enableHeliPersistence = true;
        private bool _enablePlanePersistence = true;

        // Grace windows
        private int _jetExitGraceMs = 800;
        private int _jetEnterGraceMs = 800;

        private int _propExitGraceMs = 2600;
        private int _propEnterGraceMs = 1200;

        // RPM floors
        private float _jetRpmFloorDuringTransitions = 0.25f;
        private float _propRpmFloorDuringTransitions = 0.25f;

        // Applied AFTER exit (never during entry/exit animation)
        private float _jetIdleRpm = 0.10f;

        private bool _enableDebugLogging = false;
        private string _debugLogFile = "EngineStateManager.log";

        // ===== RUNTIME =====
        private readonly Dictionary<int, TrackedAircraft> _tracked = new Dictionary<int, TrackedAircraft>(64);
        private readonly List<int> _keyBuffer = new List<int>(64);

        private string _iniAbsPath;
        private int _nextHeartbeatTime;
        private int _nextMaintenanceTime;

        public AircraftEnginePersistence()
        {
            _iniAbsPath = FindIniPath();
            LoadIniAbsolute(_iniAbsPath);

            string scriptsDir = GetScriptsDir();
            string logFile = string.IsNullOrWhiteSpace(_debugLogFile)
                ? "EngineStateManager.log"
                : Path.GetFileName(_debugLogFile.Trim());

            string preferredLog = Path.Combine(scriptsDir, logFile);
            string fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EngineStateManager");
            string fallbackLog = Path.Combine(fallbackDir, logFile);

            ModLogger.Init(_enableDebugLogging, preferredLog, fallbackLog);

            Interval = 0; // every frame to catch one-frame transitions

            Tick += OnTick;
            Aborted += OnAborted;

            if (ModLogger.Enabled)
            {
                ModLogger.Info("INI probe result:");
                ModLogger.Info("IniPathUsed=" + _iniAbsPath);
                ModLogger.Info("IniExists=" + File.Exists(_iniAbsPath));
                ModLogger.Info($"EnableHeliPersistence={_enableHeliPersistence} EnablePlanePersistence={_enablePlanePersistence}");
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            Ped ped = Game.Player?.Character;
            if (ped == null || !ped.Exists())
                return;

            int now = Game.GameTime;

            if (ModLogger.Enabled && now >= _nextHeartbeatTime)
            {
                ModLogger.Info($"Heartbeat. Tracked={_tracked.Count}");
                _nextHeartbeatTime = now + 5000;
            }

            // =========================================================
            // ENTRY PREEMPT (Planes only)
            // Goal: If the player is ENTERING a running plane, keep flags asserted
            // without ever cold-starting.
            // =========================================================
            if (_enablePlanePersistence && !ped.IsInVehicle())
            {
                // Prefer TRYING_TO_ENTER (more reliable early) then fall back to IS_ENTERING.
                int enteringHandle = 0;
                try { enteringHandle = NativeCompat.GetVehiclePedIsTryingToEnter(ped.Handle); }
                catch { enteringHandle = 0; }

                if (enteringHandle == 0)
                {
                    try { enteringHandle = Function.Call<int>(Hash.GET_VEHICLE_PED_IS_ENTERING, ped.Handle); }
                    catch { enteringHandle = 0; }
                }

                if (enteringHandle != 0 && NativeCompat.DoesEntityExist(enteringHandle))
                {
                    Vehicle enteringVeh = Entity.FromHandle(enteringHandle) as Vehicle;
                    if (enteringVeh != null && enteringVeh.Exists() && enteringVeh.ClassType == VehicleClass.Planes)
                    {
                        bool engineOn = NativeCompat.IsVehicleEngineOn(enteringHandle);

                        // Sacred: only do entry preempt if already running.
                        if (engineOn)
                        {
                            TrackedAircraft t = GetOrCreateTracked(enteringHandle, now);
                            t.ClassType = VehicleClass.Planes;

                            // If we previously spawned a ghost pilot for this prop plane, remove it now so the player doesn't 'eject' an invisible driver on entry.
                            if (t.GhostPilotPedHandle != 0)
                                CleanupGhostPilot(t, "PlayerEntering");
                            t.LastTouchedTime = now;

                            bool isPropPlane = HasPropellerBones(enteringVeh);
                            t.IsPropPlane = isPropPlane;
                            int enterGrace = isPropPlane ? _propEnterGraceMs : _jetEnterGraceMs;
                            t.EnterPreemptUntilTime = Math.Max(t.EnterPreemptUntilTime, now + enterGrace);

                            NativeCompat.SetVehicleKeepEngineOn(enteringHandle, true);

                            // Jet helper is safe to keep asserted for jets only.
                            if (!isPropPlane)
                            {
                                NativeCompat.SetVehicleJetEngineOn(enteringHandle, true);
                                NativeCompat.SetVehicleEnginePowerMultiplier(enteringHandle, 0.0f);
                                SafeForceRpmFloor(enteringVeh, t, _jetRpmFloorDuringTransitions);
                            }
                            else
                            {
                                SafeForceRpmFloor(enteringVeh, t, _propRpmFloorDuringTransitions);
                            }

                            if (ModLogger.Enabled)
                                ModLogger.InfoThrottled("enterpre_" + enteringHandle, $"EnterPreempt armed for {enteringHandle} (Planes). Until={t.EnterPreemptUntilTime}", 1000);
                        }
                    }
                }
            }

        
            // =========================================================
            // GHOST PILOT ENTER DETECTION (Prop planes)
            // Some GTA states don't report GET_VEHICLE_PED_IS_ENTERING reliably.
            // If the player is getting into ANY vehicle and is near a tracked prop plane with a ghost pilot,
            // delete the ghost immediately to avoid the player ejecting an invisible driver.
            // =========================================================
            if (_enablePlanePersistence && !ped.IsInVehicle())
            {
                bool isGettingIn = NativeCompat.IsPedGettingIntoAnyVehicle(ped.Handle);
                if (isGettingIn)
                {
                    // Deterministic target: the vehicle the ped is TRYING to enter (more reliable than nearest-vehicle heuristics).
                    int tryingHandle = 0;
                    try { tryingHandle = NativeCompat.GetVehiclePedIsTryingToEnter(ped.Handle); }
                    catch { tryingHandle = 0; }

                    if (tryingHandle != 0 && _tracked.TryGetValue(tryingHandle, out TrackedAircraft tt) && tt != null && tt.IsPropPlane && tt.GhostPilotPedHandle != 0)
                    {
                        CleanupGhostPilot(tt, "PlayerTryingToEnter");
                        tt.GhostSuppressUntilTime = now + 2500;
                        if (ModLogger.Enabled)
                            ModLogger.InfoThrottled("ghost_enterkill_" + tt.Handle, $"GhostPilot pre-delete for {tt.Handle} because player is trying to enter it.", 500);
                        // Skip nearest scan; we already handled the actual target.
                    }
                    else
                    {

                    TrackedAircraft nearest = null;
                    float nearestDist = 99999f;

                    foreach (var kv in _tracked)
                    {
                        TrackedAircraft t = kv.Value;
                        if (t == null || !t.IsPropPlane || t.GhostPilotPedHandle == 0)
                            continue;

                        if (!NativeCompat.DoesEntityExist(t.Handle))
                            continue;

                        Vehicle tv = Entity.FromHandle(t.Handle) as Vehicle;
                        if (tv == null || !tv.Exists())
                            continue;

                        float d = ped.Position.DistanceTo(tv.Position);
                        if (d < 10.0f && d < nearestDist)
                        {
                            nearest = t;
                            nearestDist = d;
                        }
                    }

                    if (nearest != null)
                    {
                        CleanupGhostPilot(nearest, "PlayerGettingIn");
                        nearest.GhostSuppressUntilTime = now + 2500;
                        if (ModLogger.Enabled)
                            ModLogger.InfoThrottled("ghost_enterkill_" + nearest.Handle, $"GhostPilot pre-delete for {nearest.Handle} because player is getting in (dist={nearestDist:0.0})", 500);
                    }
                    }

                }
            }

            // Current player vehicle (if any)
            int currentVehicleHandle = 0;
            Vehicle currentVehicle = null;

            // =========================================================
            // WHILE SEATED (arming + exit preempt)
            // =========================================================
            if (ped.IsInVehicle())
            {
                currentVehicle = ped.CurrentVehicle;
                if (currentVehicle != null && currentVehicle.Exists())
                    currentVehicleHandle = currentVehicle.Handle;

                if (currentVehicle != null && currentVehicle.Exists() && IsSupportedAircraft(currentVehicle))
                {
                    VehicleClass cls = currentVehicle.ClassType;

                    if ((cls == VehicleClass.Planes && !_enablePlanePersistence) ||
                        (cls == VehicleClass.Helicopters && !_enableHeliPersistence))
                    {
                        // gated - do nothing
                    }
                    else
                    {
                        TrackedAircraft t = GetOrCreateTracked(currentVehicleHandle, now);
                        t.ClassType = cls;
                        t.LastTouchedTime = now;
                        t.WasPlayerInsideLastUpdate = true;

                        // If back inside, clear post-exit state.
                        t.HasExited = false;
                        t.ExitGraceUntilTime = 0;
                        t.ExitPreemptUntilTime = 0;

                        bool engineOnNow = NativeCompat.IsVehicleEngineOn(currentVehicleHandle);

                        bool isPlane = (cls == VehicleClass.Planes);
                        bool isPropPlane = isPlane && HasPropellerBones(currentVehicle);
                        if (isPlane) t.IsPropPlane = isPropPlane;
                        bool isJet = isPlane && !isPropPlane;

                        // If engine appears off during enter preempt for jets, gently suppress.
                        if (isJet && !engineOnNow && now < t.EnterPreemptUntilTime)
                        {
                            NativeCompat.SetVehicleKeepEngineOn(currentVehicleHandle, true);
                            NativeCompat.SetVehicleJetEngineOn(currentVehicleHandle, true);
                            NativeCompat.SetVehicleEnginePowerMultiplier(currentVehicleHandle, 0.0f);

                            // Only hard-toggle if truly off
                            if (!NativeCompat.IsVehicleEngineOn(currentVehicleHandle))
                                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, currentVehicleHandle, true, true, false);

                            SafeForceRpmFloor(currentVehicle, t, _jetRpmFloorDuringTransitions);

                            if (ModLogger.Enabled)
                                ModLogger.InfoThrottled("entrydip_" + currentVehicleHandle, $"Entry dip suppressed for {currentVehicleHandle} (Jets).", 1000);

                            // Do NOT arm persistence from this transient; only arm when we see engine ON normally.
                        }
                        else if (!engineOnNow)
                        {
                            // Cold while seated: sacred. Never arm, clear sticky state.
                            t.EngineWasOnWhileSeated = false;
                            t.ArmedForPersistence = false;

                            t.HasExited = false;
                            t.ExitGraceUntilTime = 0;
                            t.ExitPreemptUntilTime = 0;
                            t.EnterPreemptUntilTime = 0;
                            t.NextReassertTime = 0;

                            NativeCompat.SetVehicleKeepEngineOn(currentVehicleHandle, false);
                            if (isJet) NativeCompat.SetVehicleJetEngineOn(currentVehicleHandle, false);
                        }
                        else
                        {
                            // Engine ON while seated => arm persistence.
                            t.EngineWasOnWhileSeated = true;
                            t.ArmedForPersistence = true;

                            NativeCompat.SetVehicleKeepEngineOn(currentVehicleHandle, true);
                            if (isJet) NativeCompat.SetVehicleJetEngineOn(currentVehicleHandle, true);

                            // Sample last-known RPM while seated for smoothing.
                            t.LastKnownRpm = SafeGetRpm(currentVehicle, t.LastKnownRpm);

                            // EXIT PREEMPT (Planes only, driver seat)
                            if (isPlane && ped.SeatIndex == VehicleSeat.Driver)
                            {
                                bool exitPressed = Game.IsControlPressed(Control.VehicleExit);
                                bool pedGettingOut = NativeCompat.IsPedGettingOutOfVehicle(ped.Handle);

                                if (exitPressed || pedGettingOut)
                                {
                                    int exitGrace = isPropPlane ? _propExitGraceMs : _jetExitGraceMs;

                                    t.ExitPreemptUntilTime = Math.Max(t.ExitPreemptUntilTime, now + exitGrace);
                                    t.ExitGraceUntilTime = Math.Max(t.ExitGraceUntilTime, now + exitGrace);

                                    NativeCompat.SetVehicleKeepEngineOn(currentVehicleHandle, true);

                                    if (isJet)
                                    {
                                        NativeCompat.SetVehicleJetEngineOn(currentVehicleHandle, true);
                                        NativeCompat.SetVehicleEnginePowerMultiplier(currentVehicleHandle, 0.0f);
                                        SafeForceRpmFloor(currentVehicle, t, _jetRpmFloorDuringTransitions);
                                    }
                                    else
                                    {
                                        SafeForceRpmFloor(currentVehicle, t, _propRpmFloorDuringTransitions);
                                    }

                                    if (!NativeCompat.IsVehicleEngineOn(currentVehicleHandle))
                                        Function.Call(Hash.SET_VEHICLE_ENGINE_ON, currentVehicleHandle, true, true, false);

                                    if (ModLogger.Enabled)
                                        ModLogger.InfoThrottled(
                                            "exitpre_" + currentVehicleHandle,
                                            $"ExitPreempt armed for {currentVehicleHandle} ({(isJet ? "Jets" : "Props")}). Until={t.ExitPreemptUntilTime}",
                                            500
                                        );
                                }
                            }
                        }
                    }
                }
            }

            // =========================================================
            // ENFORCEMENT LOOP (tracked aircraft)
            // =========================================================
            _keyBuffer.Clear();
            foreach (var kv in _tracked) _keyBuffer.Add(kv.Key);

            for (int i = 0; i < _keyBuffer.Count; i++)
            {
                int handle = _keyBuffer[i];

                if (!_tracked.TryGetValue(handle, out TrackedAircraft t))
                    continue;

                if (!NativeCompat.DoesEntityExist(handle))
                {
                    CleanupGhostPilot(t, "EntityMissing");
                    _tracked.Remove(handle);
                    continue;
                }

                Vehicle v = Entity.FromHandle(handle) as Vehicle;
                if (v == null || !v.Exists() || !IsSupportedAircraft(v))
                {
                    CleanupGhostPilot(t, "UnsupportedOrMissing");
                    _tracked.Remove(handle);
                    continue;
                }

                VehicleClass cls = v.ClassType;
                bool isPlane = (cls == VehicleClass.Planes);
                bool isHeli = (cls == VehicleClass.Helicopters);

                bool isPropPlane = isPlane && (t.IsPropPlane || HasPropellerBones(v));
                if (isPlane) t.IsPropPlane = isPropPlane;
                bool isJet = isPlane && !isPropPlane;

                if (isPlane && !_enablePlanePersistence) continue;
                if (isHeli && !_enableHeliPersistence) continue;

                if (_maxDistanceToTrack > 0f)
                {
                    float dist = ped.Position.DistanceTo(v.Position);
                    if (dist > _maxDistanceToTrack)
                    {
                        CleanupGhostPilot(t, "DistanceUntrack");
                    _tracked.Remove(handle);
                        continue;
                    }
                }

                bool isInsideThisAircraftNow = (currentVehicleHandle != 0 && currentVehicleHandle == handle);

                bool inEnterPreempt = isPlane && (now < t.EnterPreemptUntilTime);
                bool inPreExitGrace = isPlane && (now < t.ExitPreemptUntilTime);
                bool inPostExitGrace = isPlane && (now < t.ExitGraceUntilTime);
                bool inAnyGrace = inEnterPreempt || inPreExitGrace || inPostExitGrace;

                // Exit detection: only persist if armed.
                if (t.WasPlayerInsideLastUpdate && !isInsideThisAircraftNow)
                {
                    if (t.ArmedForPersistence)
                    {
                        t.HasExited = true;
                        t.NextReassertTime = 0;
                        t.ExitDetectedTime = now;

                        // Ensure grace window is set correctly based on plane type
                        if (isPlane)
                        {
                            int exitGrace = isPropPlane ? _propExitGraceMs : _jetExitGraceMs;
                            t.ExitGraceUntilTime = Math.Max(t.ExitGraceUntilTime, now + exitGrace);
                        }

                        if (isPropPlane)
                            EnsureGhostPilot(v, t, now);

                        if (ModLogger.Enabled)
                            ModLogger.Info($"Exit detected for {handle} ({cls}). Persistence armed.");
                    }
                    else
                    {
                        // Cold exit: clear enforcement, clear sticky flags.
                        t.HasExited = false;
                        t.ExitGraceUntilTime = 0;
                        t.ExitPreemptUntilTime = 0;
                        t.EnterPreemptUntilTime = 0;

                        NativeCompat.SetVehicleKeepEngineOn(handle, false);
                        if (isJet) NativeCompat.SetVehicleJetEngineOn(handle, false);

                        CleanupGhostPilot(t, "ColdExit");
                        if (ModLogger.Enabled)
                            ModLogger.Info($"Exit detected for {handle} ({cls}) but NOT armed (cold). No enforcement.");
                    }
                }

                t.WasPlayerInsideLastUpdate = isInsideThisAircraftNow;

                // If the player is inside this aircraft, we never want a ghost pilot occupying a seat.
                if (isInsideThisAircraftNow && t.GhostPilotPedHandle != 0)
                    CleanupGhostPilot(t, "PlayerInside");

                // If inside and not in any grace window, do not enforce.
                if (isInsideThisAircraftNow && !inAnyGrace)
                {
                    continue;
                }
    // If not in grace and not post-exit armed, do nothing.
                if (!inAnyGrace && !t.HasExited)
                    continue;

                // Prop planes: keep an invisible driver seated to prevent Rockstar empty-seat prop stall.
                if (isPropPlane && t.HasExited)
                    EnsureGhostPilot(v, t, now);

                // Rate limiting outside grace
                if (!inAnyGrace && now < t.NextReassertTime)
                    continue;

                // Maintain keep-engine-on flag during grace / post-exit
                NativeCompat.SetVehicleKeepEngineOn(handle, true);
                if (isJet) NativeCompat.SetVehicleJetEngineOn(handle, true);

                bool engineIsOn = NativeCompat.IsVehicleEngineOn(handle);

                bool shouldReassertEngine =
                    !_onlyReassertWhenEngineOff || !engineIsOn || (isPropPlane && inPostExitGrace && t.HasExited);

                if (shouldReassertEngine)
                {
                    // For planes, disableAutoStart MUST be false so we don't accidentally start cold planes
                    Function.Call(Hash.SET_VEHICLE_ENGINE_ON, handle, true, true, isPlane ? false : true);
                    Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, handle, false);
                }

                // Prop-plane post-exit: try to keep some RPM floor during grace (Rockstar may still stall)
                if (isPropPlane && inPostExitGrace && t.HasExited)
                {
                    SafeForceRpmFloor(v, t, _propRpmFloorDuringTransitions);
                }

                // Jet post-exit shaping: first 250ms hold floor; afterwards idle clamp
                if (isJet && inPostExitGrace && !isInsideThisAircraftNow)
                {
                    int sinceExit = (t.ExitDetectedTime > 0) ? (now - t.ExitDetectedTime) : 9999;

                    if (sinceExit < 250)
                        SafeForceRpmFloor(v, t, _jetRpmFloorDuringTransitions);
                    else
                        TrySetIdleRpm(v, _jetIdleRpm);
                }

                if (_forceHeliBladesFullSpeedAfterExit && isHeli && t.HasExited)
                    NativeCompat.SetHeliBladesFullSpeed(handle);

                if (ModLogger.Enabled && (inAnyGrace || !engineIsOn))
                {
                    ModLogger.InfoThrottled(
                        "enforce_" + handle,
                        $"Enforce {handle} ({cls}) EngineOn={engineIsOn} EnterGrace={inEnterPreempt} PreExitGrace={inPreExitGrace} PostExitGrace={inPostExitGrace} Armed={t.ArmedForPersistence} HasExited={t.HasExited}",
                        500
                    );
                }

                // Scheduling: every frame during grace, otherwise interval-based
                int perAircraftMs = isPlane ? Math.Min(_reassertPerAircraftMs, 150) : _reassertPerAircraftMs;
                t.NextReassertTime = inAnyGrace ? now : (now + perAircraftMs);
            }

            // =========================================================
            // MAINTENANCE / PRUNING
            // =========================================================
            if (now >= _nextMaintenanceTime)
            {
                _nextMaintenanceTime = now + Math.Max(50, _maintenanceIntervalMs);

                if (_tracked.Count > _maxTrackedAircraft)
                    PruneOldest(now);
            }
        }

    
    // =========================================================
    // Prop-plane 'ghost pilot' workaround
    // =========================================================

    // Ped model to use for the ghost pilot. Using PedHash enum avoids GetHashKey and is stable in SHVDN.
    private const PedHash GHOST_PILOT_MODEL = PedHash.Pilot01SMY;

    private void EnsureGhostPilot(Vehicle plane, TrackedAircraft t, int now)
    {
        if (plane == null || !plane.Exists())
            return;

        // Only for armed prop planes after an exit.
        if (!t.ArmedForPersistence || !t.HasExited || !t.IsPropPlane)
            return;

        int veh = plane.Handle;

        // Don't spawn/respawn while we are suppressing during enter attempts.
        if (now < t.GhostSuppressUntilTime)
            return;

        // If player is back inside, never keep the ghost around.
        Ped player = Game.Player?.Character;

        // If the player is currently entering ANY vehicle and is close to this plane, delete the ghost now.
        // This catches cases where GET_VEHICLE_PED_IS_ENTERING returns 0 but the enter task is active.
        if (player != null && player.Exists() && !player.IsInVehicle() && NativeCompat.IsPedGettingIntoAnyVehicle(player.Handle))
        {
            int trying = 0;
            try { trying = NativeCompat.GetVehiclePedIsTryingToEnter(player.Handle); }
            catch { trying = 0; }

            if (trying == veh)
            {
                CleanupGhostPilot(t, "PlayerTryingToEnterThis");
                t.GhostSuppressUntilTime = now + 2500;
                return;
            }
            float dist = player.Position.DistanceTo(plane.Position);
            if (dist < 10.0f)
            {
                CleanupGhostPilot(t, "PlayerGettingInNearThis");
                t.GhostSuppressUntilTime = now + 2500;
                return;
            }
        }

        if (player != null && player.Exists() && player.IsInVehicle() && player.CurrentVehicle != null && player.CurrentVehicle.Exists() && player.CurrentVehicle.Handle == veh)
        {
            CleanupGhostPilot(t, "PlayerReentered");
            return;
        }

        // If ghost exists and is seated as driver, we're done.
        if (t.GhostPilotPedHandle != 0 && NativeCompat.DoesEntityExist(t.GhostPilotPedHandle))
        {
            int seated = NativeCompat.GetPedInVehicleSeat(veh, -1);
            if (seated == t.GhostPilotPedHandle)
            {
                t.GhostPilotActive = true;
                return;
            }


            // Ped exists but isn't in seat: remove and recreate (rare ejection cases).
            CleanupGhostPilot(t, "GhostNotSeated");
        }

        // Request model (non-blocking). We'll spawn as soon as it's loaded.
        uint model = (uint)GHOST_PILOT_MODEL;
        if (!NativeCompat.HasModelLoaded(model))
        {
            NativeCompat.RequestModel(model);
            return;
        }

        int ghostPed = NativeCompat.CreatePedInsideVehicle(veh, model, -1);
        if (ghostPed == 0 || !NativeCompat.DoesEntityExist(ghostPed))
            return;

        // Immediately make the ped inert and invisible.
        NativeCompat.SetEntityVisible(ghostPed, false);
        NativeCompat.SetEntityAlpha(ghostPed, 0);
        NativeCompat.SetBlockingOfNonTemporaryEvents(ghostPed, true);
        NativeCompat.SetPedCanBeTargetted(ghostPed, false);
        NativeCompat.SetEntityCollision(ghostPed, false);
        NativeCompat.StopPedSpeaking(ghostPed, true);
        NativeCompat.SetAmbientVoiceName(ghostPed, "NULL");
        NativeCompat.BlockNonTemporaryEvents(ghostPed, true);

            t.GhostPilotPedHandle = ghostPed;
        t.GhostPilotActive = true;

        // Optional: free model memory once spawned.
        NativeCompat.SetModelAsNoLongerNeeded(model);

        if (ModLogger.Enabled)
            ModLogger.InfoThrottled("ghost_spawn_" + veh, $"Ghost pilot spawned for prop plane {veh} (ped={ghostPed}).", 2000);
    }

    private void CleanupGhostPilot(TrackedAircraft t, string reason)
    {
        if (t == null)
            return;

        int pedHandle = t.GhostPilotPedHandle;

        if (pedHandle != 0 && NativeCompat.DoesEntityExist(pedHandle))
        {
            NativeCompat.DeletePed(pedHandle);
        }

        t.GhostPilotPedHandle = 0;
        t.GhostPilotActive = false;

        if (ModLogger.Enabled)
            ModLogger.InfoThrottled("ghost_cleanup_" + (t.Handle != 0 ? t.Handle : 0), $"Ghost pilot cleanup. Reason={reason}", 2000);
    }

    // =========================================================
        // Helpers
        // =========================================================

        private static bool IsSupportedAircraft(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;
            VehicleClass c = v.ClassType;
            return c == VehicleClass.Planes || c == VehicleClass.Helicopters;
        }

        private static bool HasPropellerBones(Vehicle v)
        {
            if (v == null || !v.Exists())
                return false;

            int h = v.Handle;

            string[] bones =
            {
                "prop_1","prop_2","prop_3","prop_4",
                "prop","propeller","propeller1","propeller2",
                "prop_left","prop_right","prop_l","prop_r",
                "prop0","prop1","prop2","prop3","prop4"
            };

            for (int i = 0; i < bones.Length; i++)
            {
                if (NativeCompat.GetEntityBoneIndexByName(h, bones[i]) >= 0)
                    return true;
            }

            return false;
        }

        private static float SafeGetRpm(Vehicle v, float fallback)
        {
            if (v == null || !v.Exists())
                return fallback;

            try
            {
                float r = v.CurrentRPM;
                if (r < 0f) r = 0f;
                if (r > 1f) r = 1f;
                return r;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Holds RPM above a floor and never decreases RPM here (prevents perceived dip).
        /// Also never "cold starts" because it only touches RPM when a vehicle exists.
        /// </summary>
        private static void SafeForceRpmFloor(Vehicle v, TrackedAircraft t, float floor)
        {
            if (v == null || !v.Exists() || t == null)
                return;

            if (floor < 0f) floor = 0f;
            if (floor > 1f) floor = 1f;

            float current = SafeGetRpm(v, t.LastKnownRpm);
            float target = current;

            if (t.LastKnownRpm > target) target = t.LastKnownRpm;
            if (floor > target) target = floor;

            if (target > current + 0.001f)
            {
                try { v.CurrentRPM = target; } catch { }
            }

            if (target > t.LastKnownRpm)
                t.LastKnownRpm = target;
        }

        private static void TrySetIdleRpm(Vehicle v, float rpm)
        {
            if (v == null || !v.Exists())
                return;

            if (rpm < 0f) rpm = 0f;
            if (rpm > 1f) rpm = 1f;

            try { v.CurrentRPM = rpm; } catch { }
        }

        private TrackedAircraft GetOrCreateTracked(int handle, int now)
        {
            if (_tracked.TryGetValue(handle, out TrackedAircraft existing))
                return existing;

            var t = new TrackedAircraft
            {
                Handle = handle,
                ClassType = VehicleClass.Compacts,

                ArmedForPersistence = false,
                EngineWasOnWhileSeated = false,

                HasExited = false,
                WasPlayerInsideLastUpdate = false,

                NextReassertTime = 0,
                ExitGraceUntilTime = 0,
                ExitPreemptUntilTime = 0,
                EnterPreemptUntilTime = 0,
                ExitDetectedTime = 0,

                LastKnownRpm = 0.0f,
                LastTouchedTime = now
            };

            _tracked[handle] = t;

            if (ModLogger.Enabled)
                ModLogger.Info($"Tracking new aircraft {handle}");

            return t;
        }

        private void PruneOldest(int now)
        {
            int removeCount = _tracked.Count - _maxTrackedAircraft;
            if (removeCount <= 0)
                return;

            var list = new List<TrackedAircraft>(_tracked.Values);
            list.Sort((a, b) => a.LastTouchedTime.CompareTo(b.LastTouchedTime));

            for (int i = 0; i < removeCount && i < list.Count; i++)
            {
                int h = list[i].Handle;
                _tracked.Remove(h);

                if (ModLogger.Enabled)
                    ModLogger.Warn($"Pruned aircraft {h} (LRU) to maintain MaxTrackedAircraft");
            }
        }

        private void LoadIniAbsolute(string iniAbsPath)
        {
            try
            {
                ScriptSettings ini = ScriptSettings.Load(iniAbsPath);

                _maxTrackedAircraft = Clamp(ini.GetValue("Settings", "MaxTrackedAircraft", 20), 1, 200);
                _maintenanceIntervalMs = Clamp(ini.GetValue("Settings", "UpdateIntervalMs", 250), 50, 5000);
                _reassertPerAircraftMs = Clamp(ini.GetValue("Settings", "ReassertPerAircraftMs", 900), 50, 10000);

                _maxDistanceToTrack = ini.GetValue("Settings", "MaxDistanceToTrack", -1.0f);
                _onlyReassertWhenEngineOff = ini.GetValue("Settings", "OnlyReassertWhenEngineOff", true);
                _forceHeliBladesFullSpeedAfterExit = ini.GetValue("Settings", "ForceHeliBladesFullSpeedAfterExit", true);

                _enableHeliPersistence = ini.GetValue("Settings", "EnableHeliPersistence", true);
                _enablePlanePersistence = ini.GetValue("Settings", "EnablePlanePersistence", true);

                _jetExitGraceMs = Clamp(ini.GetValue("Settings", "JetExitGraceMs", 800), 0, 5000);
                _jetEnterGraceMs = Clamp(ini.GetValue("Settings", "JetEnterGraceMs", 800), 0, 5000);

                _propExitGraceMs = Clamp(ini.GetValue("Settings", "PropExitGraceMs", 2600), 0, 8000);
                _propEnterGraceMs = Clamp(ini.GetValue("Settings", "PropEnterGraceMs", 1200), 0, 8000);

                _jetIdleRpm = ini.GetValue("Settings", "JetIdleRpm", 0.18f);
                if (_jetIdleRpm < 0f) _jetIdleRpm = 0f;
                if (_jetIdleRpm > 1f) _jetIdleRpm = 1f;

                _enableDebugLogging = ini.GetValue("Settings", "EnableDebugLogging", false);
                _debugLogFile = ini.GetValue("Settings", "DebugLogFile", "EngineStateManager.log");
            }
            catch
            {
                // Defaults stay in place.
            }
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static string GetScriptsDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            baseDir = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string last = Path.GetFileName(baseDir);
            if (string.Equals(last, "scripts", StringComparison.OrdinalIgnoreCase))
                return baseDir;

            return Path.Combine(baseDir, "scripts");
        }

        private string FindIniPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string p1 = Path.Combine(baseDir, "scripts", "EngineStateManager.ini");
            if (File.Exists(p1)) return p1;

            string p2 = Path.Combine("scripts", "EngineStateManager.ini");
            if (File.Exists(p2)) return p2;

            string p3 = Path.Combine(baseDir, "EngineStateManager.ini");
            if (File.Exists(p3)) return p3;

            return p1; // default expected location
        }

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                foreach (var kv in _tracked)
                {
                    CleanupGhostPilot(kv.Value, "ScriptAbort");
                }
            }
            catch { }

    if (ModLogger.Enabled)
                ModLogger.Warn("Script aborted; clearing tracked aircraft.");

            _tracked.Clear();
            _keyBuffer.Clear();
        }
    }
}

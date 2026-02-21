using GTA;
using GTA.Native;
using System;
using System.Windows.Forms;

public sealed class EngineStateControl : Script
{
    private enum EngineOverrideState
    {
        None = 0,
        ForceOn = 1,
        ForceOff = 2
    }

    private static ScriptSettings _ini;

    // INI
    private static bool _enabled = true;
    private static bool _animationsEnabled = true;
    private static Keys _toggleKey = Keys.N;

    private EngineOverrideState _override = EngineOverrideState.None;
    private int _targetVehicleHandle = 0;

    private int _blockRestartUntilGameTime = 0;

    // Animation
    private const string AnimDict = "veh@std@ds@base";
    private const string AnimName = "change_station";
    private bool _pendingAnim = false;
    private int _animRequestUntilGameTime = 0;
    private int _queuedPedHandle = 0;
    private int _queuedAnimDuration = 650;

    public EngineStateControl()
    {
        LoadIni();

        Tick += OnTick;
        KeyUp += OnKeyUp;

        Interval = 0;

        LogInfo($"EngineStateControl loaded. Enabled={_enabled} Key={_toggleKey} Animations={_animationsEnabled}");
    }

    private static void LoadIni()
    {
        _ini = ScriptSettings.Load(@"scripts\EngineStateManager.ini");

        _enabled = _ini.GetValue("EngineToggleKeys", "ENABLED", true);
        _animationsEnabled = _ini.GetValue("EngineToggleKeys", "ANIMATIONS", true);

        string keyString = _ini.GetValue("EngineToggleKeys", "MAIN", "N");
        if (!Enum.TryParse(keyString, true, out Keys parsed))
            parsed = Keys.N;

        _toggleKey = parsed;
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (!_enabled)
        {
            if (_override != EngineOverrideState.None || _targetVehicleHandle != 0)
                ClearOverride("Feature disabled by INI.");

            _pendingAnim = false;
            return;
        }

        if (_animationsEnabled)
            ProcessPendingAnim();

        EnforceOverrideIfNeeded();
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (!_enabled)
            return;

        if (e.KeyCode != _toggleKey)
            return;

        if (IsBlockedByUI())
            return;

        ToggleForCurrentVehicle();
    }

    private void ToggleForCurrentVehicle()
    {
        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || !ped.IsInVehicle())
            return;

        Vehicle veh = ped.CurrentVehicle;
        if (veh == null || !veh.Exists())
            return;

        _targetVehicleHandle = veh.Handle;

        bool running = IsEngineRunning(veh);

        if (_animationsEnabled)
            QueueToggleAnim(ped, turnOff: running);

        _override = running ? EngineOverrideState.ForceOff : EngineOverrideState.ForceOn;

        if (_override == EngineOverrideState.ForceOff)
            _blockRestartUntilGameTime = Game.GameTime + 500;
        else
            _blockRestartUntilGameTime = 0;

        ApplyOverrideToVehicle(veh, _override);

        LogInfo($"Toggle: Veh={_targetVehicleHandle} WasRunning={running} Override={_override}");
    }

    private void EnforceOverrideIfNeeded()
    {
        if (_override == EngineOverrideState.None || _targetVehicleHandle == 0)
            return;

        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists())
        {
            ClearOverride("Player ped invalid.");
            return;
        }

        if (!ped.IsInVehicle())
        {
            ClearOverride("Player left vehicle.");
            return;
        }

        Vehicle current = ped.CurrentVehicle;
        if (current == null || !current.Exists())
        {
            ClearOverride("Current vehicle invalid.");
            return;
        }

        if (current.Handle != _targetVehicleHandle)
        {
            ClearOverride("Switched vehicles.");
            return;
        }

        if (_override == EngineOverrideState.ForceOff && IsEngineRunning(current))
        {
            ClearOverride("Engine started natively.");
            return;
        }

        if (_override == EngineOverrideState.ForceOff && Game.GameTime < _blockRestartUntilGameTime)
            return;

        ApplyOverrideToVehicle(current, _override);
    }

    private void ApplyOverrideToVehicle(Vehicle veh, EngineOverrideState state)
    {
        switch (state)
        {
            case EngineOverrideState.ForceOn:
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, true, false, false);
                break;

            case EngineOverrideState.ForceOff:
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, false, false, false);
                break;
        }
    }

    private void ClearOverride(string reason)
    {
        LogInfo($"ClearOverride: {reason}");
        _override = EngineOverrideState.None;
        _targetVehicleHandle = 0;
        _blockRestartUntilGameTime = 0;
    }

    private static bool IsEngineRunning(Vehicle veh)
        => Function.Call<bool>(Hash.GET_IS_VEHICLE_ENGINE_RUNNING, veh.Handle);

    private static bool IsBlockedByUI()
    {
        if (Game.IsPaused)
            return true;

        if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE))
            return true;

        int kb = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
        return kb == 0 || kb == 1;
    }

    // ---------- Animation ----------

    private void QueueToggleAnim(Ped ped, bool turnOff)
    {
        Function.Call(Hash.REQUEST_ANIM_DICT, AnimDict);

        _pendingAnim = true;
        _queuedPedHandle = ped.Handle;
        _queuedAnimDuration = turnOff ? 600 : 650;
        _animRequestUntilGameTime = Game.GameTime + 500;
    }

    private void ProcessPendingAnim()
    {
        if (!_pendingAnim)
            return;

        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || ped.Handle != _queuedPedHandle)
        {
            _pendingAnim = false;
            return;
        }

        if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, AnimDict))
        {
            if (Game.GameTime <= _animRequestUntilGameTime)
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, AnimDict);
                return;
            }

            _pendingAnim = false;
            return;
        }

        Function.Call(Hash.TASK_PLAY_ANIM,
            ped.Handle,
            AnimDict,
            AnimName,
            8.0f,
            1.0f,
            _queuedAnimDuration,
            48,
            0.1f,
            false, false, false);

        _pendingAnim = false;
    }

    // ---------- Logging ----------

    private static void LogInfo(string msg)
    {
        try
        {
            if (EngineStateManager.ModLogger.Enabled)
                EngineStateManager.ModLogger.Info(msg);
        }
        catch { }
    }
}
using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace EngineStateManager
{
    internal static class NativeCompat
    {
        private const ulong DOES_ENTITY_EXIST_HASH = 0x7239B21A38F536BAUL;

        // VEHICLE::SET_VEHICLE_KEEP_ENGINE_ON
        private const ulong SET_VEHICLE_KEEP_ENGINE_ON_HASH = 0xB8FBC8B1330CA9B4UL;

        // VEHICLE::IS_VEHICLE_ENGINE_ON
        private const ulong IS_VEHICLE_ENGINE_ON_HASH = 0xAE31E7DF9B5B132EUL;

        // VEHICLE::SET_VEHICLE_ENGINE_POWER_MULTIPLIER
        private const ulong SET_VEHICLE_ENGINE_POWER_MULTIPLIER_HASH = 0x93A3996368C94158UL;

        // VEHICLE::SET_HELI_BLADES_FULL_SPEED
        private const ulong SET_HELI_BLADES_FULL_SPEED_HASH = 0xA178472EBB8AE60DUL;

        // VEHICLE::SET_VEHICLE_JET_ENGINE_ON
        private const ulong SET_VEHICLE_JET_ENGINE_ON_HASH = 0x1549BA7FE83A2383UL;


        // PED::GET_VEHICLE_PED_IS_TRYING_TO_ENTER
        private const ulong GET_VEHICLE_PED_IS_TRYING_TO_ENTER_HASH = 0x814FA8BE5449445DUL;

        // Log each failing native only once to avoid spam.
        private static readonly HashSet<ulong> _failedNativeHashes = new HashSet<ulong>();

        private static void LogNativeFailureOnce(string apiName, ulong hash, Exception ex)
        {
            if (_failedNativeHashes.Contains(hash))
                return;

            _failedNativeHashes.Add(hash);

            // Keep the log message very explicit so users can report it.
            try
            {
                if (ModLogger.Enabled)
                {
                    ModLogger.Error(
                        $"Native call failed: {apiName} hash=0x{hash:X16}. " +
                        "This usually means the native isn't available in this game build or the hash is incorrect.",
                        ex);
                }
            }
            catch
            {
                // Never throw from logging.
            }
        }

        internal static bool DoesEntityExist(int handle)
        {
            if (handle == 0) return false;

            try
            {
                return Function.Call<bool>((Hash)DOES_ENTITY_EXIST_HASH, handle);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(DoesEntityExist), DOES_ENTITY_EXIST_HASH, ex);
                return false;
            }
        }

        internal static void SetVehicleKeepEngineOn(int vehicleHandle, bool toggle)
        {
            if (!DoesEntityExist(vehicleHandle)) return;

            try
            {
                Function.Call((Hash)SET_VEHICLE_KEEP_ENGINE_ON_HASH, vehicleHandle, toggle);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(SetVehicleKeepEngineOn), SET_VEHICLE_KEEP_ENGINE_ON_HASH, ex);
                // Fail soft.
            }
        }

        internal static bool IsVehicleEngineOn(int vehicleHandle)
        {
            if (!DoesEntityExist(vehicleHandle)) return false;

            try
            {
                return Function.Call<bool>((Hash)IS_VEHICLE_ENGINE_ON_HASH, vehicleHandle);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(IsVehicleEngineOn), IS_VEHICLE_ENGINE_ON_HASH, ex);
                return false;
            }
        }

        internal static void SetVehicleEnginePowerMultiplier(int vehicleHandle, float value)
        {
            if (!DoesEntityExist(vehicleHandle)) return;

            try
            {
                Function.Call((Hash)SET_VEHICLE_ENGINE_POWER_MULTIPLIER_HASH, vehicleHandle, value);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(SetVehicleEnginePowerMultiplier), SET_VEHICLE_ENGINE_POWER_MULTIPLIER_HASH, ex);
                // Fail soft.
            }
        }

        internal static void SetHeliBladesFullSpeed(int vehicleHandle)
        {
            if (!DoesEntityExist(vehicleHandle)) return;

            try
            {
                Function.Call((Hash)SET_HELI_BLADES_FULL_SPEED_HASH, vehicleHandle);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(SetHeliBladesFullSpeed), SET_HELI_BLADES_FULL_SPEED_HASH, ex);
                // Fail soft.
            }
        }

        internal static void SetVehicleJetEngineOn(int vehicleHandle, bool toggle)
        {
            if (!DoesEntityExist(vehicleHandle)) return;

            try
            {
                Function.Call((Hash)SET_VEHICLE_JET_ENGINE_ON_HASH, vehicleHandle, toggle);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(SetVehicleJetEngineOn), SET_VEHICLE_JET_ENGINE_ON_HASH, ex);
                // Fail soft.
            }
        }
        private static bool _loggedPedGetOutFail = false;

        public static bool IsPedGettingOutOfVehicle(int pedHandle)
        {
            try
            {
                // IS_PED_GETTING_OUT_OF_VEHICLE
                // Hash: 0xC7E7181C09F33B69

                return Function.Call<bool>((Hash)0xC7E7181C09F33B69, pedHandle);
            }
            catch (Exception ex)
            {
                if (!_loggedPedGetOutFail)
                {
                    _loggedPedGetOutFail = true;

                    try
                    {
                        ModLogger.Warn("Native failed: IS_PED_GETTING_OUT_OF_VEHICLE");
                        ModLogger.Error("Exception:", ex);
                    }
                    catch { }
                }

                return false;
            }
        }

        // ENTITY::GET_ENTITY_BONE_INDEX_BY_NAME
        private const ulong GET_ENTITY_BONE_INDEX_BY_NAME_HASH = 0xFB71170B7E76ACBAUL;

        internal static int GetEntityBoneIndexByName(int entityHandle, string boneName)
        {
            try
            {
                return Function.Call<int>((Hash)GET_ENTITY_BONE_INDEX_BY_NAME_HASH, entityHandle, boneName);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(GetEntityBoneIndexByName), GET_ENTITY_BONE_INDEX_BY_NAME_HASH, ex);
                return -1;
            }
        }



    // =========================================================
    // Ghost pilot helpers (prop-plane workaround)
    // =========================================================

    internal static void RequestModel(uint modelHash)
    {
        try { Function.Call(Hash.REQUEST_MODEL, modelHash); }
        catch { /* best-effort */ }
    }

    internal static bool HasModelLoaded(uint modelHash)
    {
        try { return Function.Call<bool>(Hash.HAS_MODEL_LOADED, modelHash); }
        catch { return false; }
    }

    internal static void SetModelAsNoLongerNeeded(uint modelHash)
    {
        try { Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, modelHash); }
        catch { }
    }

    internal static int CreatePedInsideVehicle(int vehicleHandle, uint modelHash, int seatIndex)
    {
        if (!DoesEntityExist(vehicleHandle)) return 0;

        try
        {
            // pedType 26 = PED_TYPE_CIVMALE (safe generic type for this inert ped)
            // isNetwork=false, bScriptHostPed=false for SP.
            return Function.Call<int>(Hash.CREATE_PED_INSIDE_VEHICLE, vehicleHandle, 26, modelHash, seatIndex, false, false);
        }
        catch (Exception ex)
        {
            // Use a synthetic hash bucket for once-only logging.
            LogNativeFailureOnce(nameof(CreatePedInsideVehicle), 0xC0DEC0DEC0DEC001UL, ex);
            return 0;
        }
    }
        internal static void StopPedSpeaking(int pedHandle, bool toggle)
        {
            try
            {
                Function.Call(Hash.STOP_PED_SPEAKING, pedHandle, toggle);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(StopPedSpeaking), 0x9D64D7405520E3D3UL, ex);
            }
        }

        internal static void SetAmbientVoiceName(int pedHandle, string voiceName)
        {
            try
            {
                Function.Call(Hash.SET_AMBIENT_VOICE_NAME, pedHandle, voiceName);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(SetAmbientVoiceName), 0x6C8065A3B7801856UL, ex);
            }
        }

        internal static void BlockNonTemporaryEvents(int pedHandle, bool toggle)
        {
            try
            {
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, pedHandle, toggle);
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(BlockNonTemporaryEvents), 0x9F8AA94D6D97DBF4UL, ex);
            }
        }

        internal static void DeletePed(int pedHandle)
        {
            try
            {
                if (pedHandle == 0)
                    return;

                Entity ent = Entity.FromHandle(pedHandle);
                if (ent == null || !ent.Exists())
                    return;

                ent.Delete();
            }
            catch (Exception ex)
            {
                LogNativeFailureOnce(nameof(DeletePed), 0xDE1E7E0000000002UL, ex);
            }
        }

        internal static void SetEntityVisible(int entityHandle, bool visible)
    {
        if (!DoesEntityExist(entityHandle)) return;
        try { Function.Call(Hash.SET_ENTITY_VISIBLE, entityHandle, visible, false); }
        catch { }
    }

    internal static void SetEntityAlpha(int entityHandle, int alpha)
    {
        if (!DoesEntityExist(entityHandle)) return;
        try { Function.Call(Hash.SET_ENTITY_ALPHA, entityHandle, alpha, false); }
        catch { }
    }

    internal static void SetBlockingOfNonTemporaryEvents(int pedHandle, bool toggle)
    {
        if (!DoesEntityExist(pedHandle)) return;
        try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, pedHandle, toggle); }
        catch { }
    }

    internal static void SetPedCanBeTargetted(int pedHandle, bool toggle)
    {
        if (!DoesEntityExist(pedHandle)) return;
        try { Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, pedHandle, toggle); }
        catch { }
    }

    internal static void SetEntityCollision(int entityHandle, bool toggle)
    {
        if (!DoesEntityExist(entityHandle)) return;
        try { Function.Call(Hash.SET_ENTITY_COLLISION, entityHandle, toggle, false); }
        catch { }
    }

    internal static int GetPedInVehicleSeat(int vehicleHandle, int seatIndex)
    {
        if (!DoesEntityExist(vehicleHandle)) return 0;
        try { return Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicleHandle, seatIndex); }
        catch { return 0; }
    }


    internal static bool IsPedGettingIntoAnyVehicle(int pedHandle)
    {
        if (!DoesEntityExist(pedHandle)) return false;
        try { return Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, pedHandle); }
        catch { return false; }
    }


        internal static int GetVehiclePedIsTryingToEnter(int pedHandle)
        {
            if (!DoesEntityExist(pedHandle)) return 0;
            try { return Function.Call<int>((Hash)GET_VEHICLE_PED_IS_TRYING_TO_ENTER_HASH, pedHandle); }
            catch { return 0; }
        }
    }

}

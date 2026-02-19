using System;
using GTA;
using GTA.Native;
using GTA.UI;

namespace PhoneHudFix
{
	public class AircraftEnginePersistence : Script
	{
		private const string CELLPHONE_CONTROLLER = "cellphone_controller";
		private const string CELLPHONE_FLASHHAND = "cellphone_flashhand";

		// Common iFruit phone txd name used by the phone UI scaleform
		private const string PHONE_TXD = "cellphone_ifruit";

		// Native hash for: _HAS_CHEAT_STRING_JUST_BEEN_ENTERED
		// (Not always present in SHVDN3's Hash enum, so we call it by raw hash.)
		private const ulong NATIVE_HAS_CHEAT_STRING_JUST_BEEN_ENTERED = 0x557E43C447E700A8;

		private readonly int fixCheatHash;

		// --- Periodic repair settings ---
		private readonly bool periodicEnabled;
		private readonly int periodicIntervalMs;
		private readonly bool periodicOnlyWhenPhoneClosed;
		private int nextPeriodicAt;

		// --- Fix pipeline state ---
		private int fixStep = 0;
		private int stepTimer = 0;
		private bool reopenAfterFix = false;
		private bool currentRunUserInitiated = false;

		public AircraftEnginePersistence()
		{
			// Avoid obsolete Game.GenerateHash in SHVDN3
			string cheatStr = Settings.GetValue("SETTINGS", "FIX_CHEAT_STRING", "fixphonehud");
			fixCheatHash = Function.Call<int>(Hash.GET_HASH_KEY, cheatStr);

			periodicEnabled = Settings.GetValue("SETTINGS", "PERIODIC_ENABLED", true);
			periodicIntervalMs = Settings.GetValue("SETTINGS", "PERIODIC_INTERVAL_MS", 60000);
			periodicOnlyWhenPhoneClosed = Settings.GetValue("SETTINGS", "PERIODIC_ONLY_WHEN_PHONE_CLOSED", true);

			nextPeriodicAt = Game.GameTime + periodicIntervalMs;

			Interval = 0;
			Tick += OnTick;
		}

		private void OnTick(object sender, EventArgs e)
		{
			// Manual trigger via cheat string
			if (HasCheatJustBeenEntered(fixCheatHash))
			{
				reopenAfterFix = IsPhoneOpen();
				StartFixPipeline(userInitiated: true);
			}

			// Periodic trigger
			if (periodicEnabled && fixStep == 0 && Game.GameTime >= nextPeriodicAt)
			{
				nextPeriodicAt = Game.GameTime + periodicIntervalMs;

				if (CanRunPeriodicFix())
				{
					reopenAfterFix = false; // periodic should not reopen phone
					StartFixPipeline(userInitiated: false);
				}
			}

			// Run staged pipeline if active
			if (fixStep != 0)
				RunFixPipeline();
		}

		private bool HasCheatJustBeenEntered(int cheatHash)
		{
			// Call by raw native hash because SHVDN3 may not expose this as Hash enum member
			return Function.Call<bool>((Hash)NATIVE_HAS_CHEAT_STRING_JUST_BEEN_ENTERED, cheatHash);
		}

		private bool CanRunPeriodicFix()
		{
			// Safer across SHVDN versions: use natives for cutscene checks
			if (Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE) || Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING))
				return false;

			if (Game.IsPaused)
				return false;

			if (periodicOnlyWhenPhoneClosed && IsPhoneOpen())
				return false;

			return true;
		}

		private void StartFixPipeline(bool userInitiated)
		{
			if (fixStep != 0) return;

			currentRunUserInitiated = userInitiated;
			fixStep = 1;
			stepTimer = 0;

			if (userInitiated)
				Screen.ShowSubtitle("Repairing phone UI...");
		}

		private void RunFixPipeline()
		{
			stepTimer++;

			switch (fixStep)
			{
				case 1:
					// Close phone to avoid mid-app resets
					ClosePhone();
					if (stepTimer > 10) NextStep();
					break;

				case 2:
					// Kill phone scripts to reset state
					Function.Call(Hash.TERMINATE_ALL_SCRIPTS_WITH_THIS_NAME, CELLPHONE_FLASHHAND);
					Function.Call(Hash.TERMINATE_ALL_SCRIPTS_WITH_THIS_NAME, CELLPHONE_CONTROLLER);
					if (stepTimer > 5) NextStep();
					break;

				case 3:
					// Recreate phone object
					Function.Call(Hash.DESTROY_MOBILE_PHONE);
					if (stepTimer > 2)
					{
						Function.Call(Hash.CREATE_MOBILE_PHONE, 0);
						NextStep();
					}
					break;

				case 4:
					// Force-reload assets (textures + scaleform)
					RequestPhoneUiAssets();
					if (stepTimer > 30) NextStep();
					break;

				case 5:
					// Restart scripts
					StartScript(CELLPHONE_CONTROLLER, 1424);
					StartScript(CELLPHONE_FLASHHAND, 1424);
					if (stepTimer > 20) NextStep();
					break;

				case 6:
					// Optional reopen if user initiated and phone was open
					if (reopenAfterFix)
						NudgePhoneOpen();

					if (currentRunUserInitiated)
						Screen.ShowSubtitle("Phone UI refresh complete");

					fixStep = 0;
					break;
			}
		}

		private void NextStep()
		{
			fixStep++;
			stepTimer = 0;
		}

		private void StartScript(string scriptName, int stackSize)
		{
			Function.Call(Hash.REQUEST_SCRIPT, scriptName);

			for (int i = 0; i < 120; i++)
			{
				if (Function.Call<bool>(Hash.HAS_SCRIPT_LOADED, scriptName))
					break;

				Wait(1);
			}

			if (Function.Call<bool>(Hash.HAS_SCRIPT_LOADED, scriptName))
			{
				Function.Call(Hash.START_NEW_SCRIPT, scriptName, stackSize);
				Function.Call(Hash.SET_SCRIPT_AS_NO_LONGER_NEEDED, scriptName);
			}
		}

		private void RequestPhoneUiAssets()
		{
			Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, PHONE_TXD, true);

			// Phone UI scaleform(s)
			Function.Call<int>(Hash.REQUEST_SCALEFORM_MOVIE, "cellphone_ifruit");
			Function.Call<int>(Hash.REQUEST_SCALEFORM_MOVIE, "cellphone_ifruit_2");
		}

		private bool IsPhoneOpen()
		{
			// Heuristic: phone-related state tends to flip when phone UI is active.
			// This native exists broadly and is safer than the old 2-arg control helpers.
			return Function.Call<bool>(Hash.IS_MOBILE_PHONE_RADIO_ACTIVE);
		}

		private void ClosePhone()
		{
			// Back out of phone/cellcam/frontend
			Function.Call(Hash.CELL_CAM_ACTIVATE, false, false);
			Function.Call(Hash.SET_MOBILE_PHONE_RADIO_STATE, false);
			Function.Call(Hash.SET_FRONTEND_ACTIVE, false);
			Function.Call(Hash.SET_PAUSE_MENU_ACTIVE, false);
		}

		private void NudgePhoneOpen()
		{
			// Don’t hard-force open (can be flaky). Just ensure the phone control is enabled this frame.
			// Use native control enable to avoid SHVDN overload differences.
			Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.Phone, true);
		}
	}
}
﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Assets.Scripts.Progression;
using Assets.Scripts.Settings;
using Assets.Scripts.BombBinder;
using Assets.Scripts.Mods.Mission;
using Assets.Scripts.Leaderboards;
using Assets.Scripts.Services.Steam;

[RequireComponent(typeof(KMService))]
[RequireComponent(typeof(KMGameInfo))]
class Tweaks : MonoBehaviour
{
	public static ModConfig<TweakSettings> modConfig;
	public static TweakSettings settings;
	public static bool CaseGeneratorSettingCache; // The CaseGenerator setting is cached until the user returns to the setup room to fix bugs related to the largest case size being cached.

	public static bool TwitchPlaysActive => GameObject.Find("TwitchPlays_Info") != null;
	public static Mode CurrentMode => TwitchPlaysActive ? Mode.Normal : settings.Mode;

	public static KMGameInfo GameInfo;
	[HideInInspector]
	public KMGameInfo.State CurrentState;

	private readonly HashSet<TableOfContentsMetaData> ModToCMetaData = new HashSet<TableOfContentsMetaData>();

	static GameObject SettingWarning;
	GameObject TweaksCaseGeneratorCase;

	public void Awake()
	{
		MainThreadQueue.Initialize();

		GameInfo = GetComponent<KMGameInfo>();
		SettingWarning = transform.Find("UI").Find("SettingWarning").gameObject;
		BetterCasePicker.BombCaseGenerator = GetComponentInChildren<BombCaseGenerator>();

		modConfig = new ModConfig<TweakSettings>("TweakSettings");
		UpdateSettings();

		bool changeFadeTime = settings.FadeTime >= 0;

		FreeplayDevice.MAX_SECONDS_TO_SOLVE = float.MaxValue;
		FreeplayDevice.MIN_MODULE_COUNT = 1;

		if (settings.EnableModsOnlyKey)
		{
			var lastFreeplaySettings = FreeplaySettings.CreateDefaultFreeplaySettings();
			lastFreeplaySettings.OnlyMods = true;
			ProgressionManager.Instance.RecordLastFreeplaySettings(lastFreeplaySettings);
		}

		UpdateSettingWarning();

		// Setup API/properties other mods to interact with
		GameObject infoObject = new GameObject("Tweaks_Info", typeof(TweaksProperties));
		infoObject.transform.parent = gameObject.transform;

		// Watch the TweakSettings file for Time Mode state being changed in the office.
		FileSystemWatcher watcher = new FileSystemWatcher(Path.Combine(Application.persistentDataPath, "Modsettings"), "TweakSettings.json")
		{
			NotifyFilter = NotifyFilters.LastWrite
		};
		watcher.Changed += (object source, FileSystemEventArgs e) =>
		{
			if (modConfig.SerializeSettings(settings) == modConfig.SerializeSettings(modConfig.Settings)) return;

			UpdateSettings();
			UpdateSettingWarning();

			MainThreadQueue.Enqueue(() => StartCoroutine(ModifyFreeplayDevice(false)));
		};

		// Setup our "service" to block the leaderboard submission requests
		ReflectedTypes.InstanceField.SetValue(null, new SteamFilterService());

		// Create a fake case with a bunch of anchors to trick the game when using CaseGenerator.
		TweaksCaseGeneratorCase = new GameObject("TweaksCaseGenerator");
		TweaksCaseGeneratorCase.transform.SetParent(transform);
		var kmBomb = TweaksCaseGeneratorCase.AddComponent<KMBomb>();
		kmBomb.IsHoldable = false;
		kmBomb.WidgetAreas = new List<GameObject>();
		kmBomb.visualTransform = transform;
		kmBomb.Faces = new List<KMBombFace>();

		TweaksCaseGeneratorCase.AddComponent<ModBomb>();

		var kmBombFace = TweaksCaseGeneratorCase.AddComponent<KMBombFace>();
		kmBombFace.Anchors = new List<Transform>();
		kmBomb.Faces.Add(kmBombFace);

		for (int i = 0; i <= 9001; i++) kmBombFace.Anchors.Add(transform);

		// Handle scene changes
		UnityEngine.SceneManagement.SceneManager.sceneLoaded += (Scene scene, LoadSceneMode _) =>
		{
			UpdateSettings();
			UpdateSettingWarning();

			Modes.settings = Modes.modConfig.Settings;
			Modes.modConfig.Settings = Modes.settings;

			if ((scene.name == "mainScene" || scene.name == "gameplayScene") && changeFadeTime) SceneManager.Instance.RapidFadeInTime = settings.FadeTime;

			switch (scene.name)
			{
				case "mainScene":
					if (changeFadeTime)
					{
						SceneManager.Instance.SetupState.FadeInTime =
						SceneManager.Instance.SetupState.FadeOutTime =
						SceneManager.Instance.UnlockState.FadeInTime = settings.FadeTime;
					}

					break;
				case "gameplayLoadingScene":
					var gameplayLoadingManager = FindObjectOfType<GameplayLoadingManager>();
					if (settings.InstantSkip) gameplayLoadingManager.MinTotalLoadTime = 0;
					if (changeFadeTime)
					{
						gameplayLoadingManager.FadeInTime =
						gameplayLoadingManager.FadeOutTime = settings.FadeTime;
					}

					ReflectedTypes.UpdateTypes();

					ReflectedTypes.CurrencyAPIEndpointField?.SetValue(null, settings.FixFER ? "http://api.exchangeratesapi.io" : "http://api.fixer.io");

					break;
				case "gameplayScene":
					if (changeFadeTime)
					{
						SceneManager.Instance.GameplayState.FadeInTime =
						SceneManager.Instance.GameplayState.FadeOutTime = settings.FadeTime;
					}

					break;
			}
		};

		// Handle state changes
		GameInfo.OnStateChange += (KMGameInfo.State state) =>
		{
			CurrentState = state;
			watcher.EnableRaisingEvents = state == KMGameInfo.State.Setup;

			if (state == KMGameInfo.State.Gameplay)
			{
				bool disableRecords = settings.BombHUD || settings.ShowEdgework || CurrentMode != Mode.Normal || settings.MissionSeed != -1;

				Assets.Scripts.Stats.StatsManager.Instance.DisableStatChanges =
				Assets.Scripts.Records.RecordManager.Instance.DisableBestRecords = disableRecords;
				if (disableRecords) SteamFilterService.TargetMissionID = GameplayState.MissionToLoad;

				BetterCasePicker.HandleCaseGeneration();

				BombStatus.Instance.widgetsActivated = false;
				BombStatus.Instance.HUD.SetActive(settings.BombHUD);
				BombStatus.Instance.Edgework.SetActive(settings.ShowEdgework);
				BombStatus.Instance.ConfidencePrefab.gameObject.SetActive(CurrentMode != Mode.Zen);
				BombStatus.Instance.StrikesPrefab.color = CurrentMode == Mode.Time ? Color.yellow : Color.red;

				Modes.Multiplier = Modes.settings.TimeModeStartingMultiplier;
				BombStatus.Instance.UpdateMultiplier();
				bombWrappers = new BombWrapper[] { };
				StartCoroutine(CheckForBombs());
				if (settings.SkipGameplayDelay) StartCoroutine(SkipGameplayDelay());

				if (GameplayState.BombSeedToUse == -1) GameplayState.BombSeedToUse = settings.MissionSeed;
			}
			else if (state == KMGameInfo.State.Setup)
			{
				if (ReflectedTypes.LoadedModsField.GetValue(ModManager.Instance) is Dictionary<string, Mod> loadedMods)
				{
					Mod tweaksMod = loadedMods.Values.FirstOrDefault(mod => mod.ModID == "Tweaks");
					if (tweaksMod != null)
					{
						if (CaseGeneratorSettingCache != settings.CaseGenerator)
						{
							if (settings.CaseGenerator)
								tweaksMod.ModObjects.Add(TweaksCaseGeneratorCase);
							else
								tweaksMod.ModObjects.Remove(TweaksCaseGeneratorCase);

							CaseGeneratorSettingCache = settings.CaseGenerator;
							UpdateSettingWarning();
						}
					}
				}

				StartCoroutine(ModifyFreeplayDevice(true));
				GetComponentInChildren<ModSelectorExtension>().FindAPI();

				GameplayState.BombSeedToUse = -1;
			}
			else if (state == KMGameInfo.State.Transitioning)
			{
				// Because the settings are checked on a scene change and there is no scene change from exiting the gameplay room,
				// we need to update the settings here in case the user changed their HideTOC settings.
				UpdateSettings();

				bool modified = false;
				var ModMissionToCs = ModManager.Instance.ModMissionToCs;
				foreach (var metaData in ModMissionToCs)
					modified |= ModToCMetaData.Add(metaData);

				var unloadedMods = (Dictionary<string, Mod>) ReflectedTypes.UnloadedModsField.GetValue(ModManager.Instance);
				if (unloadedMods != null)
					foreach (var unloadedMod in unloadedMods.Values)
					{
						var tocs = (List<ModTableOfContentsMetaData>) ReflectedTypes.TocsField.GetValue(unloadedMod);
						if (tocs != null)
							foreach (var metaData in tocs)
								modified |= ModToCMetaData.Remove(metaData);
					}

				var newToCs = ModToCMetaData.Where(metaData => !settings.HideTOC.Any(pattern => Localization.GetLocalizedString(metaData.DisplayNameTerm).Like(pattern)));
				modified |= (newToCs.Count() != ModMissionToCs.Count || !newToCs.All(ModMissionToCs.Contains));
				ModMissionToCs.Clear();
				ModMissionToCs.AddRange(newToCs);

				if (modified)
				{
					SetupState.LastBombBinderTOCIndex = 0;
					SetupState.LastBombBinderTOCPage = 0;
				}
			}
		};
	}

	// TODO: Remove this
	/*
	Vector2 scrollPosition;
	Vector2 scrollPosition2;
	GameObject inspecting;
	Dictionary<GameObject, bool> ExpandedObjects = new Dictionary<GameObject, bool>();
	void OnGUI()
	{
		GUILayout.BeginHorizontal();
		scrollPosition = GUILayout.BeginScrollView(scrollPosition);
		foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
		{
			DisplayChildren(root);
		}
		GUILayout.EndScrollView();

		if (inspecting)
		{
			scrollPosition2 = GUILayout.BeginScrollView(scrollPosition2);
			foreach (Component component in inspecting.GetComponents<Component>())
			{
				GUILayout.Label(component.GetType().Name);
				foreach (System.Reflection.FieldInfo fieldInfo in component.GetType().GetFields())
				{
					if (typeof(Array).IsAssignableFrom(fieldInfo.FieldType))
					{
						try
						{
							foreach (object obj in (Array) fieldInfo.GetValue(component))
							{
								GUILayout.Label("    * " + obj);
							}
						} catch { }
					}
					else
					{
						GUILayout.Label(" - " + fieldInfo.Name + " = " + fieldInfo.GetValue(component));
					}
				}
			}
			GUILayout.EndScrollView();
		}
		GUILayout.EndHorizontal();
	}

	void DisplayChildren(GameObject gameObj)
	{
		GUILayout.BeginHorizontal();
		ExpandedObjects.TryGetValue(gameObj, out bool expanded);
		expanded = GUILayout.Toggle(expanded, gameObj.name + (gameObj.activeInHierarchy ? " [E]" : " [X]"));
		ExpandedObjects[gameObj] = expanded;

		if (GUILayout.Button("Inspect")) inspecting = gameObj;
		GUILayout.EndHorizontal();

		if (!expanded) return;

		GUILayout.BeginHorizontal();
		GUILayout.Space(10);
		GUILayout.BeginVertical();
		foreach (Transform child in gameObj.transform)
		{
			GameObject childObj = child.gameObject;

			DisplayChildren(childObj);
		}

		GUILayout.EndVertical();
		GUILayout.EndHorizontal();
	}*/

	public static BombWrapper[] bombWrappers = new BombWrapper[] { };

	public IEnumerator CheckForBombs()
	{
		yield return new WaitUntil(() => (SceneManager.Instance.GameplayState.Bombs?.Count > 0));
		yield return null;
		List<Bomb> bombs = SceneManager.Instance.GameplayState.Bombs;

		void wrapInitialBombs()
		{
			Array.Resize(ref bombWrappers, bombs.Count);

			for (int i = 0; i < bombs.Count; i++)
			{
				Bomb bomb = bombs[i];
				BombWrapper bombWrapper = new BombWrapper(bomb);
				bombWrappers[i] = bombWrapper;
				bombWrapper.holdable.OnLetGo += () => BombStatus.Instance.currentBomb = null;

				if (CurrentMode == Mode.Time) bombWrapper.CurrentTimer = Modes.settings.TimeModeStartingTime * 60;
				else if (CurrentMode == Mode.Zen) bombWrapper.CurrentTimer = 0.001f;
			}
		}

		if (CurrentMode == Mode.Zen)
		{
			GameplayMusicController gameplayMusic = MusicManager.Instance.GameplayMusicController;
			gameplayMusic.StopMusic();
			var controller = gameplayMusic.GetComponent<DarkTonic.MasterAudio.PlaylistController>();
			controller.ClearQueue();
			controller.QueuePlaylistClip(controller.CurrentPlaylist.MusicSettings[0].songName, true);
		}

		if (ReflectedTypes.FactoryRoomType != null && ReflectedTypes.StaticModeType != null)
		{
			UnityEngine.Object factoryRoom = FindObjectOfType(ReflectedTypes.FactoryRoomType);
			if (factoryRoom)
			{
				if (ReflectedTypes.FactoryRoomDataType != null && ReflectedTypes.WarningTimeField != null)
				{
					var roomData = FindObjectOfType(ReflectedTypes.FactoryRoomDataType);
					if (roomData != null)
						ReflectedTypes.WarningTimeField.SetValue(roomData, CurrentMode == Mode.Zen ? 0 : 60);
				}

				object gameMode = ReflectedTypes.GameModeProperty.GetValue(factoryRoom, null);
				if (ReflectedTypes.StaticModeType != gameMode.GetType())
				{
					IEnumerable<object> adaptations = ((IEnumerable) ReflectedTypes.AdaptationsProperty.GetValue(gameMode, null)).Cast<object>();
					bool globalTimerDisabled = !adaptations.Any(adaptation => ReflectedTypes.GlobalTimerAdaptationType.IsAssignableFrom(adaptation.GetType()));

					Component getBomb() => (Component) ReflectedTypes._CurrentBombField.GetValue(gameMode);

					yield return new WaitUntil(() => getBomb() != null || factoryRoom == null);
					Component currentBomb = getBomb();
					bool firstBomb = true;

					Array.Resize(ref bombWrappers, 1);

					while (currentBomb != null && factoryRoom != null)
					{
						BombWrapper bombWrapper = new BombWrapper(currentBomb.GetComponent<Bomb>());
						bombWrappers[0] = bombWrapper;
						bombWrapper.holdable.OnLetGo += () => BombStatus.Instance.currentBomb = null;

						if (globalTimerDisabled || firstBomb)
						{
							firstBomb = false;

							if (CurrentMode == Mode.Time)
								bombWrapper.CurrentTimer = Modes.settings.TimeModeStartingTime * 60;
							else if (CurrentMode == Mode.Zen)
								bombWrapper.CurrentTimer = 0.001f;
						}

						yield return new WaitUntil(() => currentBomb != getBomb() || factoryRoom == null);

						currentBomb = getBomb();

						if (currentBomb == null || factoryRoom == null) break;
					}
				}
				else
				{
					wrapInitialBombs();
				}

				yield break;
			}
		}

		// This code only runs if we aren't in the Factory room.
		wrapInitialBombs();

		SceneManager.Instance.GameplayState.Room.PacingActions.RemoveAll(pacingAction => pacingAction.EventType == Assets.Scripts.Pacing.PaceEvent.OneMinuteLeft);
		UnityEngine.Object portalRoom = null;
		if (ReflectedTypes.PortalRoomType != null && ReflectedTypes.RedLightsMethod != null && ReflectedTypes.RoomLightField != null)
		{
			portalRoom = FindObjectOfType(ReflectedTypes.PortalRoomType);
		}
		bool lastState = false;
		IEnumerator portalEmergencyRoutine = null;
		while (CurrentState == KMGameInfo.State.Gameplay)
		{
			bool targetState = CurrentMode != Mode.Zen && bombWrappers.Any((BombWrapper bombWrapper) => !bombWrapper.Bomb.IsSolved() && bombWrapper.CurrentTimer < 60f);
			if (targetState != lastState)
			{
				foreach (Assets.Scripts.Props.EmergencyLight emergencyLight in FindObjectsOfType<Assets.Scripts.Props.EmergencyLight>())
				{
					if (targetState)
					{
						emergencyLight.Activate();
					}
					else
					{
						emergencyLight.Deactivate();
					}
				}
				if (portalRoom != null)
				{
					if (targetState)
					{
						portalEmergencyRoutine = (IEnumerator) ReflectedTypes.RedLightsMethod.Invoke(portalRoom, null);
						StartCoroutine(portalEmergencyRoutine);
					}
					else
					{
						StopCoroutine(portalEmergencyRoutine);
						portalEmergencyRoutine = null;
						((GameObject) ReflectedTypes.RoomLightField.GetValue(portalRoom)).GetComponent<Light>().color = new Color(0.5f, 0.5f, 0.5f);
					}
				}
				lastState = targetState;
			}
			yield return null;
		}
	}

	public IEnumerator SkipGameplayDelay()
	{
		yield return null;
		Time.timeScale = 100;
		yield return new WaitForSeconds(6);
		Time.timeScale = 1;
	}

	static float originalTime = 300;
	public static IEnumerator ModifyFreeplayDevice(bool firstTime)
	{
		yield return null;
		SetupRoom setupRoom = FindObjectOfType<SetupRoom>();
		if (setupRoom)
		{
			FreeplayDevice freeplayDevice = setupRoom.FreeplayDevice;
			ExecOnDescendants(freeplayDevice.gameObject, gameObj =>
			{
				string gameObjName = gameObj.name;
				if (gameObjName == "FreeplayLabel" || gameObjName == "Free Play Label")
					gameObj.GetComponent<TMPro.TextMeshPro>().text = CurrentMode == Mode.Normal ? Localization.GetLocalizedString($"FreeplayDevice/label_free{(gameObjName == "FreeplayLabel" ? "playInnerTitle" : "PlayCover")}") : $"{CurrentMode.ToString()} mode";
			});

			freeplayDevice.CurrentSettings.Time = CurrentMode == Mode.Time ? Modes.settings.TimeModeStartingTime * 60 : originalTime;
			TimeSpan timeSpan = TimeSpan.FromSeconds(freeplayDevice.CurrentSettings.Time);
			freeplayDevice.TimeText.text = string.Format("{0}:{1:00}", (int) timeSpan.TotalMinutes, timeSpan.Seconds);

			if (!firstTime) yield break;
			if (CurrentMode == Mode.Normal) originalTime = freeplayDevice.CurrentSettings.Time;

			freeplayDevice.TimeIncrement.OnPush += delegate { ReflectedTypes.IsInteractingField.SetValue(freeplayDevice.TimeIncrement, true); };
			freeplayDevice.TimeIncrement.OnInteractEnded += delegate
			{
				originalTime = freeplayDevice.CurrentSettings.Time;
				if (CurrentMode != Mode.Time) return;

				Modes.settings.TimeModeStartingTime = freeplayDevice.CurrentSettings.Time / 60;
				Modes.modConfig.Settings = Modes.settings;
			};

			freeplayDevice.TimeDecrement.OnPush += delegate { ReflectedTypes.IsInteractingField.SetValue(freeplayDevice.TimeDecrement, true); };
			freeplayDevice.TimeDecrement.OnInteractEnded += delegate
			{
				originalTime = freeplayDevice.CurrentSettings.Time;
				if (CurrentMode != Mode.Time) return;

				Modes.settings.TimeModeStartingTime = freeplayDevice.CurrentSettings.Time / 60;
				Modes.modConfig.Settings = Modes.settings;
			};
		}
	}

	public static void UpdateSettings()
	{
		settings = modConfig.Settings;
		modConfig.Settings = settings; // Write any settings that the user doesn't have in their settings file.
	}

	public void Update() => MainThreadQueue.ProcessQueue();

	void UpdateSettingWarning() => MainThreadQueue.Enqueue(() => SettingWarning.SetActive(CurrentState == KMGameInfo.State.Setup && CaseGeneratorSettingCache != settings.CaseGenerator));

	/*void OnApplicationQuit()
	{
		Debug.LogFormat("[Tweaks] [OnApplicationQuit] Found output_log: {0}", File.Exists(Path.Combine(Application.dataPath, "output_log.txt")));
	}*/

	public static void Log(params object[] args) => Debug.Log("[Tweaks] " + args.Select(Convert.ToString).Join(" "));

	static void ExecOnDescendants(GameObject gameObj, Action<GameObject> func)
	{
		foreach (Transform child in gameObj.transform)
		{
			GameObject childObj = child.gameObject;
			func(childObj);

			ExecOnDescendants(childObj, func);
		}
	}

	void LogChildren(Transform goTransform, int depth = 0)
	{
		Log($"{new string('\t', depth)}{goTransform.name} - {goTransform.localPosition.ToString("N6")}");
		foreach (Transform child in goTransform)
		{
			LogChildren(child, depth + 1);
		}
	}

	public static Dictionary<string, object>[] TweaksEditorSettings = new Dictionary<string, object>[]
	{
		new Dictionary<string, object>
		{
			{ "Filename", "TweakSettings.json" },
			{ "Name", "Tweaks" },
			{ "Listings", new List<Dictionary<string, object>>
				{
					new Dictionary<string, object>
					{
						{ "Key", "Mode" },
						{ "Description", "Sets the mode for the next round." },
						{ "Type", "Dropdown" },
						{ "DropdownItems", new List<object> { "Normal", "Time", "Zen", "Steady" } }
					},
					new Dictionary<string, object> { { "Key", "FadeTime" }, { "Text", "Fade Time" }, { "Description", "The number seconds should it take to fade in and out of scenes." } },
					new Dictionary<string, object> { { "Key", "InstantSkip" }, { "Text", "Instant Skip" }, { "Description", "Skips the gameplay loading screen as soon as possible." } },
					new Dictionary<string, object> { { "Key", "SkipGameplayDelay" }, { "Text", "Skip Gameplay Delay" }, { "Description", "Skips the delay at the beginning of a round when the lights are out." } },
					new Dictionary<string, object> { { "Key", "BetterCasePicker" }, { "Text", "Better Case Picker" }, { "Description", "Chooses the smallest case that fits instead of a random one." } },
					new Dictionary<string, object> { { "Key", "EnableModsOnlyKey" }, { "Text", "Enable Mods Only Key" }, { "Description", "Turns the Mods Only key to be on by default." } },
					new Dictionary<string, object> { { "Key", "FixFER" }, { "Text", "Fix Foreign Exchange Rates" }, { "Description", "Changes the URL that is queried since the old one is no longer operational." } },
					new Dictionary<string, object> { { "Key", "BombHUD" }, { "Text", "Bomb HUD" }, { "Description", "Adds a HUD in the top right corner showing information about the currently selected bomb." } },
					new Dictionary<string, object> { { "Key", "ShowEdgework" }, { "Text", "Show Edgework" }, { "Description", "Adds a HUD to the top of the screen showing the edgework for the currently selected bomb." } },
					new Dictionary<string, object> { { "Key", "MissionSeed" }, { "Text", "Mission Seed" }, { "Description", "Seeds the random numbers for the mission which should make the bomb\ngenerate consistently." } },
					new Dictionary<string, object> { { "Key", "CaseGenerator" }, { "Text", "Case Generator" }, { "Description", "Generates a case to best fit the bomb which can be one of the colors defined by CaseColors." } },
				}
			}
		},
		new Dictionary<string, object>
		{
			{ "Filename", "ModeSettings.json" },
			{ "Name", "Mode Settings" },
			{ "Listings", new List<Dictionary<string, object>>
				{
					new Dictionary<string, object> { { "Text", "Zen Mode" }, { "Type", "Section" } },
					new Dictionary<string, object> { { "Key", "ZenModeTimePenalty" }, { "Text", "Time Penalty" }, { "Description", "The base amount of minutes to be penalized for getting a strike." } },
					new Dictionary<string, object> { { "Key", "ZenModeTimePenaltyIncrease" }, { "Text", "Time Penalty Increase" }, { "Description", "The number of minutes to add to the penalty each time you get\na strike after the first." } },
					new Dictionary<string, object> { { "Key", "ZenModeTimerSpeedUp" }, { "Text", "Timer Speed Up" }, { "Description", "The rate the timer speeds up when you get a strike." } },
					new Dictionary<string, object> { { "Key", "ZenModeTimerMaxSpeed" }, { "Text", "Timer Max Speed" }, { "Description", "The maximum rate the timer can be set to.\nFor example, 2 is twice as fast as the normal timer." } },

					new Dictionary<string, object> { { "Text", "Steady Mode" }, { "Type", "Section" } },
					new Dictionary<string, object> { { "Key", "SteadyModeFixedPenalty" }, { "Text", "Fixed Penalty" }, { "Description", "The number of minutes subtracted from the time when you get a strike." } },
					new Dictionary<string, object> { { "Key", "SteadyModePercentPenalty" }, { "Text", "Percent Penalty" }, { "Description", "The factor of the starting time the remaining time is reduced by." } },

					new Dictionary<string, object> { { "Text", "Time Mode" }, { "Type", "Section" } },
					new Dictionary<string, object> { { "Key", "TimeModeStartingTime" }, { "Text", "Starting Time" }, { "Description", "The number of minutes on the timer when you start a bomb." } },
					new Dictionary<string, object> { { "Key", "TimeModeStartingMultiplier" }, { "Text", "Starting Multiplier" }, { "Description", "The initial multiplier." } },
					new Dictionary<string, object> { { "Key", "TimeModeMaxMultiplier" }, { "Text", "Max Multiplier" }, { "Description", "The highest the multiplier can go." } },
					new Dictionary<string, object> { { "Key", "TimeModeMinMultiplier" }, { "Text", "Min Multiplier" }, { "Description", "The lowest the multiplier can go." } },
					new Dictionary<string, object> { { "Key", "TimeModeSolveBonus" }, { "Text", "Solve Bonus" }, { "Description", "The amount added to the multiplier when you solve a module." } },
					new Dictionary<string, object> { { "Key", "TimeModeMultiplierStrikePenalty" }, { "Text", "Multiplier Strike Penalty" }, { "Description", "The amount subtracted from the multiplier when you get a\nstrike." } },
					new Dictionary<string, object> { { "Key", "TimeModeTimerStrikePenalty" }, { "Text", "Timer Strike Penalty" }, { "Description", "The factor the time is reduced by when getting a strike." } },
					new Dictionary<string, object> { { "Key", "TimeModeMinimumTimeLost" }, { "Text", "Min Time Lost" }, { "Description", "Lowest amount of time that you can lose when you get a strike." } },
					new Dictionary<string, object> { { "Key", "TimeModeMinimumTimeGained" }, { "Text", "Min Time Gained" }, { "Description", "Lowest amount of time you can gain when you solve a module." } },
				}
			}
		}
	};
}

class SteamFilterService : ServicesSteam
{
	public static string TargetMissionID;

	public override void ExecuteLeaderboardRequest(LeaderboardRequest request)
	{
		LeaderboardListRequest listRequest = request as LeaderboardListRequest;
		if (listRequest?.SubmitScore == true && listRequest?.MissionID == TargetMissionID)
		{
			ReflectedTypes.SubmitFieldProperty.SetValue(listRequest, false, null);

			TargetMissionID = null;
		}

		base.ExecuteLeaderboardRequest(request);
	}
}

class TweakSettings
{
	public float FadeTime = 1f;
	public bool InstantSkip = true;
	public bool SkipGameplayDelay = false;
	public bool BetterCasePicker = true;
	public bool EnableModsOnlyKey = false;
	public bool FixFER = false;
	public bool BombHUD = false;
	public bool ShowEdgework = false;
	public List<string> HideTOC = new List<string>();
	public Mode Mode = Mode.Normal;
	public int MissionSeed = -1;
	public bool CaseGenerator = true;
	public List<string> CaseColors = new List<string>();
	public HashSet<string> PinnedSettings = new HashSet<string>();
}
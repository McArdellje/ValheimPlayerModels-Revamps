﻿#if PLUGIN
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;
using UnityEngine;
using ValheimPlayerModels.Loaders;
using BepInEx.Logging;

namespace ValheimPlayerModels
{
    [BepInPlugin(PluginConfig.PLUGIN_GUID, PluginConfig.PLUGIN_NAME, PluginConfig.PLUGIN_VERSION)]
    [BepInProcess("valheim.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        public static string playerModelsPath => Path.Combine(Environment.CurrentDirectory, "PlayerModels");

        public static Dictionary<string, string> playerModelBundlePaths { get; private set; }
        public static Dictionary<string, AvatarLoaderBase> playerModelBundleCache = new Dictionary<string, AvatarLoaderBase>();
        public static Dictionary<Character,PlayerModel> playerModelCharacters = new Dictionary<Character,PlayerModel>();

        public static bool showActionMenu;
        public static bool showAvatarMenu;
        private Rect ActionWindowRect;
        private Rect AvatarWindowRect;
        private static CursorLockMode oldCursorLockState = CursorLockMode.None;
        private static bool oldCursorVisible = true;
        public const int ActionWindowId = -48;
        public const int AvatarWindowId = -49;
        private Vector2 actionMenuWindowScrollPos;
        private Vector2 avatarMenuWindowScrollPos;
        
        public static bool DisplayingWindow =>
            showActionMenu || showAvatarMenu;

        private void Awake()
        {
            Log = Logger;
            UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds = 5000000;
            PluginConfig.InitConfig(Config);

            Config.SettingChanged += ConfigOnSettingChanged;

            if (!Directory.Exists(playerModelsPath))
                Directory.CreateDirectory(playerModelsPath);

            RefreshBundlePaths(false);

            var harmony = new Harmony(PluginConfig.PLUGIN_GUID+".patch");
            harmony.PatchAll();

            Logger.LogInfo($"Plugin {PluginConfig.PLUGIN_GUID} is loaded!");
        }

        private void Update()
        {
            if (PluginConfig.reloadKey.Value.IsDown())
            {
                if (PluginConfig.enablePlayerModels.Value)
                {
                    PlayerModel[] playerModels = FindObjectsOfType<PlayerModel>();
                    bool canReload = true;

                    foreach (PlayerModel playerModel in playerModels)
                    {
                        if (!playerModel.playerModelLoaded)
                        {
                            canReload = false;
                            break;
                        }
                    }

                    if (!canReload) return;

                    foreach (PlayerModel playerModel in playerModels)
                    {
                        playerModel.ToggleAvatar(false);
                        Destroy(playerModel);
                    }

                    foreach (AvatarLoaderBase loader in playerModelBundleCache.Values)
                    {
                        if(loader != null) loader.Unload();
                    }
                    playerModelBundleCache.Clear();
                    RefreshBundlePaths();

                    Player[] players = FindObjectsOfType<Player>();
                    foreach (Player player in players)
                    {
                        player.gameObject.AddComponent<PlayerModel>();
                    }
                }
            }

            if (PluginConfig.actionMenuKey.Value.IsDown() && !showAvatarMenu)
            {
                if (PlayerModel.localModel != null && Game.instance != null)
                {
                    showActionMenu = !showActionMenu;
                    if (showActionMenu)
                    {
                        SetUnlockCursor();
                        GUI.FocusWindow(ActionWindowId);
                    }
                    else ResetCursor();
                }
            }

            if (PluginConfig.avatarMenuKey.Value.IsDown() && !showActionMenu)
            {
                showAvatarMenu = !showAvatarMenu;
                if (showAvatarMenu)
                {
                    SetUnlockCursor();
                    GUI.FocusWindow(AvatarWindowId);
                }
                else ResetCursor();
            }
        }

        private void ConfigOnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e.ChangedSetting.Definition.Key == "EnablePlayerModels")
            {
                PlayerModel[] playerModels = FindObjectsOfType<PlayerModel>();
                if ((bool) e.ChangedSetting.BoxedValue)
                {
                    foreach (PlayerModel playerModel in playerModels)
                    {
                        playerModel.ToggleAvatar();
                    }

                    Player[] players = FindObjectsOfType<Player>();
                    foreach (Player player in players)
                    {
                        if (player.GetComponent<PlayerModel>() == null)
                        {
                            player.gameObject.AddComponent<PlayerModel>();
                        }
                    }
                }
                else
                {
                    foreach (PlayerModel playerModel in playerModels)
                    {
                        playerModel.ToggleAvatar(false);
                    }
                }
            }
            else if (e.ChangedSetting.Definition.Key == "SelectedAvatar")
            {
                PlayerModel playerModel = null;

                if (Player.m_localPlayer != null)
                    playerModel = Player.m_localPlayer.gameObject.GetComponent<PlayerModel>();
                else
                    playerModel = FindObjectOfType<PlayerModel>();

                if (playerModel != null)
                {
                    if (playerModel.playerModelLoaded)
                    {
                        playerModel.ReloadAvatar();
                    }
                }
            }
        }

        public static void RefreshBundlePaths(bool silent = true)
        {
            playerModelBundlePaths = new Dictionary<string, string>();

            string[] files = Directory.GetFiles(playerModelsPath, "*.*", SearchOption.AllDirectories)
                .Where(s => s.EndsWith(".valavtr") || s.EndsWith(".vrm")).ToArray();

            foreach (string file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file).ToLower();

                if (!silent)
                    Log.LogMessage("Found avatar : " + fileName);
                if(!playerModelBundlePaths.ContainsKey(fileName))
                    playerModelBundlePaths.Add(fileName, file);
            }
        }

        #region GUI
        
        public static void SetUnlockCursor()
        {
            if (Cursor.lockState == CursorLockMode.None) return;

            oldCursorLockState = Cursor.lockState;
            oldCursorVisible = Cursor.visible;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public static void ResetCursor()
        {
            Cursor.lockState = oldCursorLockState;
            Cursor.visible = oldCursorVisible;
        }

        private bool ShouldCloseWindow(Rect windowRect) {
            var guiMousePosition = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return GUI.Button(new Rect(0, 0, Screen.width, Screen.height), string.Empty, GUIStyle.none) &&
                !windowRect.Contains(guiMousePosition) || Input.GetKeyDown(KeyCode.Escape);
        }

        private void OnGUI()
        {
            if (showActionMenu && PlayerModel.localModel != null)
            {
                if (ActionWindowRect.width == 0) {
                    ActionWindowRect = new Rect(Screen.width, Screen.height, 250, 400);
                    ActionWindowRect.x -= ActionWindowRect.width;
                    ActionWindowRect.y -= ActionWindowRect.height;
                }
                

                if (ShouldCloseWindow(ActionWindowRect))
                {
                    showActionMenu = false;
                    ResetCursor();
                }

                GUI.enabled = PlayerModel.localModel.playerModelLoaded;

                GUI.Box(ActionWindowRect, GUIContent.none);
                ActionWindowRect = GUILayout.Window(Plugin.ActionWindowId, ActionWindowRect, ActionMenuWindow, "Action Menu");

                Input.ResetInputAxes();
            }

            if (showAvatarMenu)
            {
                if (AvatarWindowRect.width == 0) {
                    AvatarWindowRect = new Rect(20, 100, 250, 400);
                }

                if (ShouldCloseWindow(AvatarWindowRect))
                {
                    showAvatarMenu = false;
                    ResetCursor();
                }

                if (PlayerModel.localModel)
                    GUI.enabled = PlayerModel.localModel.playerModelLoaded;
                else
                    GUI.enabled = true;

                GUI.Box(AvatarWindowRect, GUIContent.none);
                AvatarWindowRect = GUILayout.Window(AvatarWindowId, AvatarWindowRect, AvatarMenuWindow, "Avatar Menu");

                Input.ResetInputAxes();
            }
        }

        private void ActionMenuWindow(int id)
        {
            GUI.DragWindow (new Rect (0, 0, 10000, 20));
            AvatarInstance avatar = PlayerModel.localModel.avatar;
            actionMenuWindowScrollPos = GUILayout.BeginScrollView(actionMenuWindowScrollPos, false, true);

            var scrollPosition = actionMenuWindowScrollPos.y;
            var scrollHeight = ActionWindowRect.height;

            GUILayout.BeginVertical();

            float controlHeight = 21;
            float currentHeight = 0;
            
            var setParams = new HashSet<int>();

            for (int i = 0; i < avatar.MenuControls.Count; i++)
            {
                int paramId = Animator.StringToHash(avatar.MenuControls[i].parameter);

                if (string.IsNullOrEmpty(avatar.MenuControls[i].name) ||
                    string.IsNullOrEmpty(avatar.MenuControls[i].parameter) ||
                    !avatar.Parameters.ContainsKey(paramId)) continue;

                var visible = controlHeight == 0 || currentHeight + controlHeight >= scrollPosition && currentHeight <= scrollPosition + scrollHeight;

                if (visible)
                {
                    try {
                        GUILayout.BeginHorizontal(GUI.skin.box);
                        GUILayout.Label(avatar.MenuControls[i].name);

                        float parameterValue = avatar.GetParameterValue(paramId);

                        switch (avatar.MenuControls[i].type) {
                            case ControlType.Button:
                                if (GUILayout.RepeatButton(string.Empty)) {
                                    avatar.SetParameterValue(paramId, avatar.MenuControls[i].value);
                                    setParams.Add(paramId);
                                } else if (!setParams.Contains(paramId)) {
                                    avatar.SetParameterValue(paramId, 0);
                                }

                                break;
                            case ControlType.Toggle:
                                bool menuToggleValue = parameterValue != 0;

                                bool toggleValue = GUILayout.Toggle(menuToggleValue, string.Empty);
                                if (toggleValue != menuToggleValue) {
                                    avatar.SetParameterValue(paramId, toggleValue ? avatar.MenuControls[i].value : 0);
                                    setParams.Add(paramId);
                                }

                                break;
                            case ControlType.Slider:

                                float sliderValue = GUILayout.HorizontalSlider(parameterValue, 0.0f, 1.0f);
                                if (Mathf.Abs(sliderValue - parameterValue) > 0.01f) {
                                    avatar.SetParameterValue(paramId, sliderValue);
                                    setParams.Add(paramId);
                                }

                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        GUILayout.EndHorizontal();
                    } catch (ArgumentException e) {
                        Log.LogError(e);
                    }
                }
                else
                {
                    try {
                        GUILayout.Space(controlHeight);
                    } catch (ArgumentException e) {
                        Log.LogError(e);
                    }
                }

                currentHeight += controlHeight;
            }

            GUILayout.Space(70);

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void AvatarMenuWindow(int id)
        {
            GUI.DragWindow (new Rect (0, 0, 10000, 20));
            avatarMenuWindowScrollPos = GUILayout.BeginScrollView(avatarMenuWindowScrollPos, false, true);

            var scrollPosition = avatarMenuWindowScrollPos.y;
            var scrollHeight = AvatarWindowRect.height;

            GUILayout.BeginVertical();

            float controlHeight = 21;
            float currentHeight = controlHeight;

            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label("Automatic");

            if (PluginConfig.selectedAvatar.Value == "")
                GUI.enabled = false;

            if (GUILayout.Button("Select"))
            {
                PluginConfig.selectedAvatar.BoxedValue = "";
            }

            GUI.enabled = true;

            GUILayout.EndHorizontal();

            foreach (string avatar in Plugin.playerModelBundlePaths.Keys)
            {
                var visible = controlHeight == 0 || currentHeight + controlHeight >= scrollPosition && currentHeight <= scrollPosition + scrollHeight;

                if (visible)
                {
                    try
                    {
                        GUILayout.BeginHorizontal(GUI.skin.box);
                        GUILayout.Label(avatar);

                        if (PlayerModel.localModel.selectedAvatar == avatar)
                            GUI.enabled = false;

                        if (GUILayout.Button("Select"))
                        {
                            PluginConfig.selectedAvatar.BoxedValue = avatar;
                        }

                        GUI.enabled = true;

                        GUILayout.EndHorizontal();
                    }
                    catch (ArgumentException) { }
                }
                else
                {
                    try
                    {
                        GUILayout.Space(controlHeight);
                    }
                    catch (ArgumentException) { }
                }

                currentHeight += controlHeight;
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        #endregion

    }
}
#endif
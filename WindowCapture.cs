using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using HarmonyLib;
using Klak.Spout;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(WIGUx.Modules.WindowCaptureModule.WindowCapture), "WindowCaptureMod", "2.0.0", "Earth")]
[assembly: MelonGame("EmuVR", "EmuVR")]

namespace WIGUx.Modules.WindowCaptureModule
{
    public class WindowCapture : MelonMod
    {
        public static WindowCapture Instance { get; private set; }

        // --- Reflection Cache for Klak.Spout ---
        public static Type _pluginEntryType;
        public static Type _utilType;
        public static Type _eventType;
        public static MethodInfo _checkValidMethod;
        public static MethodInfo _createReceiverMethod;
        public static MethodInfo _getTexturePointerMethod;
        public static MethodInfo _getTextureWidthMethod;
        public static MethodInfo _getTextureHeightMethod;
        public static MethodInfo _issuePluginEventMethod;
        public static MethodInfo _destroyMethod;

        public override void OnInitializeMelon()
        {
            Instance = this;
            CaptureCore.Initialize();
            CaptureCore.Logger.Msg("[WindowCapture] Initialized.");
            
            // Initialize Reflection for Spout internals
            try
            {
                CaptureCore.Logger.Msg("[WindowCapture] Initializing Spout reflection...");
                var assembly = typeof(Klak.Spout.SpoutReceiver).Assembly;
                _pluginEntryType = assembly.GetType("Klak.Spout.PluginEntry");
                _utilType = assembly.GetType("Klak.Spout.Util");
                _eventType = assembly.GetType("Klak.Spout.PluginEntry+Event"); // Nested enum

                if (_pluginEntryType != null && _utilType != null && _eventType != null)
                {
                    _checkValidMethod = _pluginEntryType.GetMethod("CheckValid", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _createReceiverMethod = _pluginEntryType.GetMethod("CreateReceiver", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _getTexturePointerMethod = _pluginEntryType.GetMethod("GetTexturePointer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _getTextureWidthMethod = _pluginEntryType.GetMethod("GetTextureWidth", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _getTextureHeightMethod = _pluginEntryType.GetMethod("GetTextureHeight", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                    _issuePluginEventMethod = _utilType.GetMethod("IssuePluginEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _destroyMethod = _utilType.GetMethod("Destroy", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    CaptureCore.Logger.Msg("[WindowCapture] Spout reflection successful.");
                }
                else
                {
                    CaptureCore.Logger.Error("[WindowCapture] Could not find internal Spout types via Reflection.");
                }
            }
            catch (Exception e)
            {
                CaptureCore.Logger.Error($"[WindowCapture] Error initializing Spout reflection: {e}");
            }
        }
    }

    public class WindowCaptureInstance : MonoBehaviour
    {
        private Process captureProcess;
        private Process gameProcess;

        private SpoutReceiver spoutReceiver;

        private GameSystem gameSystem;
        private ScreenController screenController;
        private MeshRenderer screenRenderer;
        private MaterialPropertyBlock screenPropertyBlock;
        
        private FieldInfo screenAspectRatioField;
        private FieldInfo overscanField;

        void OnDestroy()
        {
            if (captureProcess != null && !captureProcess.HasExited)
            {
                CaptureCore.Logger.Msg("[CaptureCore] Killing capture process.");
                try { captureProcess.Kill(); } catch {}
                captureProcess = null;
            }
            if (gameProcess != null && !gameProcess.HasExited)
            {
                CaptureCore.Logger.Msg("[CaptureCore] Killing game process.");
                try { gameProcess.Kill(); } catch {}
                gameProcess = null;
            }
            if (spoutReceiver != null) Destroy(spoutReceiver);
            if (screenController != null)
            {
                screenController.receivingTexture = false;
                screenController.EnableOffTexture();
            }
            if (screenRenderer != null) screenRenderer.SetPropertyBlock(null);
        }

        public void Initialize(string windowName, bool hideCursor, string launchPath)
        {
            // 0. Launch Game Process if requested
            if (!string.IsNullOrEmpty(launchPath))
            {
                // Always resolve to a full path to avoid ambiguity with Process.Start
                string fullLaunchPath = Path.GetFullPath(launchPath);

                if (File.Exists(fullLaunchPath))
                {
                    try
                    {
                        string workingDir = Path.GetDirectoryName(fullLaunchPath);
                        ProcessStartInfo gameInfo = new ProcessStartInfo(fullLaunchPath);
                        gameInfo.WorkingDirectory = workingDir;
                        gameProcess = Process.Start(gameInfo);
                        CaptureCore.Logger.Msg($"[CaptureCore] Launched game: {fullLaunchPath}");
                    }
                    catch (Exception e)
                    {
                        CaptureCore.Logger.Error($"[CaptureCore] Failed to launch game: {e.Message}");
                    }
                }
                else
                {
                    CaptureCore.Logger.Error($"[CaptureCore] Could not find game to launch at: {fullLaunchPath} (from original path: {launchPath})");
                }
            }

            bool isExternalSpout = windowName.StartsWith("spout:", StringComparison.OrdinalIgnoreCase);

            string spoutSourceName;
            if (isExternalSpout)
            {
                spoutSourceName = windowName.Substring(6);
                CaptureCore.Logger.Msg($"[CaptureCore] Connecting to external Spout sender: {spoutSourceName}");
            }
            else
            {
                spoutSourceName = "GameCaptureWGC";
                // Start the capture loop to wait for the window and handle launchers
                StartCoroutine(CaptureLoop(windowName, hideCursor));
            }

            // 2. Setup Spout Receiver (Video)
            spoutReceiver = gameObject.AddComponent<SpoutReceiver>();
            spoutReceiver.sourceName = spoutSourceName;
        }

        private IEnumerator CaptureLoop(string windowName, bool hideCursor)
        {
            string exePath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserData", "WindowCapture", "SpoutWGCSender.exe");
            if (!File.Exists(exePath))
            {
                CaptureCore.Logger.Error($"[CaptureCore] SpoutWGCSender.exe not found at: {exePath}");
                yield break;
            }

            // Kill any orphaned SpoutWGCSender processes to prevent conflicts
            foreach (var proc in Process.GetProcessesByName("SpoutWGCSender"))
            {
                try { proc.Kill(); } catch { }
            }

            string target = windowName.Replace("\"", "");
            string args = $"\"{target}\"";
            if (hideCursor) args += " --no-cursor";

            ProcessStartInfo startInfo = new ProcessStartInfo(exePath)
            {
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            while (true)
            {
                float startTime = Time.time;
                CaptureCore.Logger.Msg($"[CaptureCore] Launching SpoutWGCSender: {exePath} {args}");

                bool processStarted = false;

                try
                {
                    captureProcess = Process.Start(startInfo);

                    captureProcess.OutputDataReceived += (sender, e) => {
                        if (!string.IsNullOrEmpty(e.Data)) CaptureCore.Logger.Msg($"[SpoutWGCSender] {e.Data}");
                    };
                    captureProcess.ErrorDataReceived += (sender, e) => {
                        if (!string.IsNullOrEmpty(e.Data)) CaptureCore.Logger.Error($"[SpoutWGCSender ERROR] {e.Data}");
                    };
                    captureProcess.BeginOutputReadLine();
                    captureProcess.BeginErrorReadLine();

                    CaptureCore.Logger.Msg($"[CaptureCore] Started SpoutWGCSender process (PID: {captureProcess.Id})");
                    processStarted = true;
                }
                catch (Exception e)
                {
                    CaptureCore.Logger.Error($"[CaptureCore] Failed to start SpoutWGCSender.exe: {e.Message}");
                }

                if (!processStarted)
                {
                    yield return new WaitForSeconds(5f);
                    continue;
                }

                // Wait for process to exit
                while (captureProcess != null && !captureProcess.HasExited)
                {
                    yield return new WaitForSeconds(0.5f);
                }

                float runDuration = Time.time - startTime;
                
                // Heuristic: If it ran for > 2 seconds, assume it connected successfully and the game just closed.
                // If less, assume it failed to find the window (game starting up, or launcher active).
                if (runDuration > 2f)
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] Capture process exited after {runDuration:F2}s. Shutting down system.");
                    if (gameSystem != null && gameSystem.SystemState.IsOn)
                    {
                        gameSystem.SystemState.Power(false);
                    }
                    UnityEngine.Object.Destroy(this);
                    yield break;
                }
                else
                {
                    // It exited quickly, likely window not found. Retry.
                    yield return new WaitForSeconds(1f);
                }
            }
        }

        void Start()
        {
            gameSystem = GetComponent<GameSystem>();
            if (gameSystem == null) { Destroy(this); return; }

            if (gameSystem.IsUsingEmbeddedScreen)
                SetScreenController(GetComponent<ScreenController>());
            else if (gameSystem.Screen != null)
                SetScreenController(gameSystem.Screen.GetComponent<ScreenController>());
        }

        void LateUpdate()
        {
            // Self-destruct if the game is no longer a wgc game
            if (gameSystem != null)
            {
                if (gameSystem.Game == null || gameSystem.Game.core != "wgc_libretro")
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] Game is no longer a window capture game. Destroying instance.");
                    UnityEngine.Object.Destroy(this);
                    return;
                }
            }

            if (gameSystem.Screen != null && screenController?.gameObject != gameSystem.Screen)
                SetScreenController(gameSystem.Screen.GetComponent<ScreenController>());
            else if (gameSystem.Screen == null && screenController != null)
                SetScreenController(null);

            if (screenController == null || screenRenderer == null) return;

            Texture receivedTexture = spoutReceiver?.receivedTexture;
            bool hasTexture = receivedTexture != null && receivedTexture.width > 8;
            
            // Check if the system is running
            if (hasTexture && gameSystem.retroarchIsRunning)
            {
                // Fix for static/black screen: Ensure ScreenController knows we are providing a texture and TV is on.
                if (!screenController.receivingTexture) screenController.receivingTexture = true;
                if (!screenController.NetworktvOn) screenController.NetworktvOn = true;
                
                // Force the screen material to ensure we aren't rendering on the static/off material
                if (screenRenderer.sharedMaterial != screenController.screenMaterial)
                {
                    screenRenderer.sharedMaterial = screenController.screenMaterial;
                }

                // CRITICAL FIX: Do NOT get the current property block from the renderer.
                // It contains leftover scaling/offset values from the static animation which cause stretching.
                // Instead, start with a clean block to reset all unspecified properties to material defaults.
                screenPropertyBlock.Clear();
                screenPropertyBlock.SetColor("_Color", Color.black);
                screenPropertyBlock.SetColor("_EmissionColor", Color.white);
                screenPropertyBlock.SetTexture("_EmissionMap", receivedTexture);
                
                // CRITICAL FIX: Reset EmissionMap scale/offset. 
                // ScreenController modifies this for static noise, which distorts our capture if not reset.
                screenPropertyBlock.SetVector("_EmissionMap_ST", new Vector4(1, 1, 0, 0));
                // Reset texture scale/offset to prevent interference from static animation
                screenPropertyBlock.SetVector("_MainTex_ST", new Vector4(1, 1, 0, 0));

                // Aspect Ratio Correction
                if (screenAspectRatioField != null && overscanField != null)
                {
                    float screenAspect = (float)screenAspectRatioField.GetValue(screenController);
                    Vector2 overscan = (Vector2)overscanField.GetValue(screenController);
                    if (screenAspect <= 0.001f) screenAspect = 1.3333f;
                    float texAspect = (float)receivedTexture.width / receivedTexture.height;
                    float ratio = texAspect / screenAspect;
                    Vector2 stretch = ratio < 1.0f ? new Vector2(1.0f / ratio, 1.0f) : new Vector2(1.0f, ratio);
                    screenPropertyBlock.SetVector("_ScreenStretch", new Vector4(overscan.x * stretch.x, overscan.y * stretch.y, 0, 0));
                }

                screenRenderer.SetPropertyBlock(screenPropertyBlock);
            }
            else
            {
                if (screenController.receivingTexture)
                {
                    screenController.receivingTexture = false;
                    screenRenderer.SetPropertyBlock(null);
                }
            }
        }

        private void SetScreenController(ScreenController sc)
        {
            if (screenController != null && screenController != sc)
            {
                screenController.receivingTexture = false;
                screenController.EnableOffTexture();
            }

            screenController = sc;

            if (screenController != null)
            {
                var renderers = screenController.GetComponentsInChildren<Renderer>();
                foreach (var r in renderers)
                {
                    if (r.sharedMaterial == screenController.screenMaterial)
                    {
                        screenRenderer = r as MeshRenderer;
                        break;
                    }
                }
                if (screenRenderer == null) screenRenderer = screenController.GetComponentInChildren<MeshRenderer>();

                screenPropertyBlock = new MaterialPropertyBlock();
                screenAspectRatioField = typeof(ScreenController).GetField("screenAspectRatio", BindingFlags.NonPublic | BindingFlags.Instance);
                overscanField = typeof(ScreenController).GetField("overscan", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            else
            {
                screenRenderer = null;
                screenPropertyBlock = null;
            }
        }
    }

    public static class CaptureCore
    {
        public static MelonLogger.Instance Logger;
        private static HarmonyLib.Harmony harmony;

        public static void Initialize()
        {
            Logger = new MelonLogger.Instance("WindowCaptureMod");
            harmony = new HarmonyLib.Harmony("com.earth.windowcapture");

            // Patch Retroarch.Open
            var retroarchOpenOriginal = typeof(Retroarch).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Open" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
            if (retroarchOpenOriginal != null)
            {
                var retroarchOpenPrefix = typeof(CaptureCore).GetMethod(nameof(OnRetroarchOpenPrefix));
                harmony.Patch(retroarchOpenOriginal, prefix: new HarmonyMethod(retroarchOpenPrefix));
                Logger.Msg("[CaptureCore] Patched Retroarch.Open.");
            } else {
                Logger.Error("[CaptureCore] Could not find Retroarch.Open method to patch!");
            }

            // Patch Retroarch.Close
            var retroarchCloseOriginal = typeof(Retroarch).GetMethod("Close", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (retroarchCloseOriginal != null)
            {
                var retroarchClosePrefix = typeof(CaptureCore).GetMethod(nameof(OnRetroarchClosePrefix));
                harmony.Patch(retroarchCloseOriginal, prefix: new HarmonyMethod(retroarchClosePrefix));
                Logger.Msg("[CaptureCore] Patched Retroarch.Close.");
            } else {
                Logger.Error("[CaptureCore] Could not find Retroarch.Close method to patch!");
            }

            var spoutUpdateOriginal = typeof(Klak.Spout.SpoutReceiver).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
            var spoutUpdatePrefix = typeof(CaptureCore).GetMethod(nameof(OnSpoutUpdatePrefix));
            harmony.Patch(spoutUpdateOriginal, new HarmonyMethod(spoutUpdatePrefix));
            Logger.Msg("[CaptureCore] Patched SpoutReceiver.Update successfully.");
        }

        public static bool OnRetroarchOpenPrefix(Retroarch __instance)
        {
            // Check if this is a game we should handle
            if (__instance.game == null || __instance.game.core != "wgc_libretro")
            {
                return true; // Not our game, run original Open method
            }

            Logger.Msg($"[CaptureCore] Retroarch.Open intercepted for '{__instance.game.name}'.");

            // Prevent original Retroarch.Open from running
            try
            {
                GameSystem gameSystem = __instance.GetComponent<GameSystem>();
                if (gameSystem != null)
                {
                    string path = gameSystem.Game.path;
                    string windowName = "";
                    bool hideCursor = false;
                    string launchPath = "";

                    string extension = Path.GetExtension(path).ToLower();
                    if (extension == ".win" || extension == ".txt")
                    {
                        string content = "";
                        if (File.Exists(path))
                        {
                            try {
                                content = File.ReadAllText(path, System.Text.Encoding.UTF8).Trim();
                                content = content.Replace("\uFEFF", ""); 
                            } catch { }
                        }

                        if (content.Contains("--no-cursor"))
                        {
                            hideCursor = true;
                            content = content.Replace("--no-cursor", "");
                        }

                        windowName = content.Trim();

                        string batPath = Path.ChangeExtension(path, ".bat");
                        if (File.Exists(batPath))
                        {
                            launchPath = batPath;
                        }
                    }
                    else if (extension == ".bat" || extension == ".exe" || extension == ".lnk" || extension == ".url")
                    {
                        launchPath = path;
                        windowName = Path.GetFileNameWithoutExtension(path);
                    }
                    else
                    {
                        windowName = Path.GetFileNameWithoutExtension(path);
                    }

                    // Unquote if necessary
                    if (windowName.Length > 1 && windowName.StartsWith("\"") && windowName.EndsWith("\""))
                    {
                        windowName = windowName.Substring(1, windowName.Length - 2);
                    }

                    Logger.Msg($"[CaptureCore] Initializing capture for: '{windowName}', Hide Cursor: {hideCursor}, Launch: '{launchPath}'");

                    var capture = gameSystem.gameObject.GetComponent<WindowCaptureInstance>() ?? gameSystem.gameObject.AddComponent<WindowCaptureInstance>();
                    capture.Initialize(windowName, hideCursor, launchPath);

                    // Manually set the 'isRunning' flag so the game knows the system is on
                    var isRunningField = typeof(Retroarch).GetField("isRunning", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (isRunningField != null)
                    {
                        isRunningField.SetValue(__instance, true);
                    }
                    else
                    {
                        Logger.Warning("[CaptureCore] Could not find Retroarch 'isRunning' field.");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"[CaptureCore] Error in OnRetroarchOpenPrefix: {e.Message}");
            }

            return false; // Skip original method
        }

        public static bool OnRetroarchClosePrefix(Retroarch __instance)
        {
            if (__instance.game == null || __instance.game.core != "wgc_libretro")
            {
                return true; // Not our game, run original Close method
            }

            Logger.Msg($"[CaptureCore] Retroarch.Close intercepted for '{__instance.game.name}'.");

            var capture = __instance.GetComponent<WindowCaptureInstance>();
            if (capture != null)
            {
                UnityEngine.Object.Destroy(capture);
            }
            
            // The original method sets isRunning to false, so we can just let it run.
            return true; 
        }

        public static bool OnSpoutUpdatePrefix(Klak.Spout.SpoutReceiver __instance)
        {
            try
            {
                var type = typeof(Klak.Spout.SpoutReceiver);
                var pluginField = type.GetField("_plugin", BindingFlags.NonPublic | BindingFlags.Instance);
                var sourceNameField = type.GetField("_sourceName", BindingFlags.NonPublic | BindingFlags.Instance);
                var sharedTextureField = type.GetField("_sharedTexture", BindingFlags.NonPublic | BindingFlags.Instance);
                var receivedTextureField = type.GetField("_receivedTexture", BindingFlags.NonPublic | BindingFlags.Instance);
                var targetTextureField = type.GetField("_targetTexture", BindingFlags.NonPublic | BindingFlags.Instance);
                var targetRendererField = type.GetField("_targetRenderer", BindingFlags.NonPublic | BindingFlags.Instance);
                var targetMaterialPropertyField = type.GetField("_targetMaterialProperty", BindingFlags.NonPublic | BindingFlags.Instance);
                var propertyBlockField = type.GetField("_propertyBlock", BindingFlags.NonPublic | BindingFlags.Instance);

                var _checkValidMethod = WindowCapture._checkValidMethod;
                var _createReceiverMethod = WindowCapture._createReceiverMethod;
                var _getTexturePointerMethod = WindowCapture._getTexturePointerMethod;
                var _getTextureWidthMethod = WindowCapture._getTextureWidthMethod;
                var _getTextureHeightMethod = WindowCapture._getTextureHeightMethod;
                var _issuePluginEventMethod = WindowCapture._issuePluginEventMethod;
                var _destroyMethod = WindowCapture._destroyMethod;
                var _eventType = WindowCapture._eventType;

                if (_checkValidMethod == null)
                {
                    Logger.Error("[CaptureCore] Spout reflection methods not found. Aborting fallback.");
                    return false;
                }

                IntPtr _plugin = (IntPtr)pluginField.GetValue(__instance);
                string _sourceName = (string)sourceNameField.GetValue(__instance);

                if (_plugin != IntPtr.Zero && !(bool)_checkValidMethod.Invoke(null, new object[] { _plugin }))
                {
                    object disposeEvent = Enum.ToObject(_eventType, 1);
                    _issuePluginEventMethod.Invoke(null, new object[] { disposeEvent, _plugin });
                    _plugin = IntPtr.Zero;
                }
                if (_plugin == IntPtr.Zero)
                {
                    _plugin = (IntPtr)_createReceiverMethod.Invoke(null, new object[] { _sourceName });
                    if (_plugin == IntPtr.Zero) return false;
                    pluginField.SetValue(__instance, _plugin);
                }

                object updateEvent = Enum.ToObject(_eventType, 0);
                _issuePluginEventMethod.Invoke(null, new object[] { updateEvent, _plugin });

                IntPtr texturePointer = (IntPtr)_getTexturePointerMethod.Invoke(null, new object[] { _plugin });
                int textureWidth = (int)_getTextureWidthMethod.Invoke(null, new object[] { _plugin });
                int textureHeight = (int)_getTextureHeightMethod.Invoke(null, new object[] { _plugin });

                Texture2D _sharedTexture = (Texture2D)sharedTextureField.GetValue(__instance);
                RenderTexture _receivedTexture = (RenderTexture)receivedTextureField.GetValue(__instance);

                if (_sharedTexture != null && (textureWidth != _sharedTexture.width || textureHeight != _sharedTexture.height))
                {
                    _destroyMethod.Invoke(null, new object[] { _sharedTexture });
                    if (!__instance.keepLastFrameOnTextureLost) _destroyMethod.Invoke(null, new object[] { _receivedTexture });
                    _sharedTexture = null;
                    sharedTextureField.SetValue(__instance, null);
                }

                if (_sharedTexture == null && texturePointer != IntPtr.Zero)
                {
                    _sharedTexture = Texture2D.CreateExternalTexture(textureWidth, textureHeight, TextureFormat.ARGB32, false, false, texturePointer);
                    _sharedTexture.hideFlags = HideFlags.DontSave;
                    sharedTextureField.SetValue(__instance, _sharedTexture);
                    _destroyMethod.Invoke(null, new object[] { _receivedTexture });
                }
                else if (_sharedTexture == null && _receivedTexture != null && !__instance.keepLastFrameOnTextureLost)
                {
                    _destroyMethod.Invoke(null, new object[] { _receivedTexture });
                }

                if (_sharedTexture != null)
                {
                    Vector2 scale = new Vector2(1, -1);
                    Vector2 offset = new Vector2(0, 1);

                    RenderTexture _targetTexture = (RenderTexture)targetTextureField.GetValue(__instance);
                    if (_targetTexture != null)
                    {
                        Graphics.Blit(_sharedTexture, _targetTexture, scale, offset);
                    }
                    else
                    {
                        if (_receivedTexture == null)
                        {
                            // FIX: Use the default (linear) color space to match the game's rendering pipeline.
                            _receivedTexture = new RenderTexture(_sharedTexture.width, _sharedTexture.height, 0);
                            _receivedTexture.hideFlags = HideFlags.DontSave;
                            receivedTextureField.SetValue(__instance, _receivedTexture);
                        }
                        Graphics.Blit(_sharedTexture, _receivedTexture, scale, offset);
                    }
                }

                // The rest of the original method is for applying to a target renderer, which we don't use.
            }
            catch (Exception e)
            {
                Logger.Error($"[CaptureCore] Exception in OnSpoutUpdatePrefix: {e}");
                return false; // Prevent original from running and crashing
            }

            return false; // Skip original Update
        }
    }
}

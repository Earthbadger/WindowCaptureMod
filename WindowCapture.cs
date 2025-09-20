using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using System.Diagnostics;
using EmuVR.InputManager;
using MelonLoader;
using WIGU;

[assembly: MelonInfo(typeof(WIGUx.Modules.WindowCaptureModule.CaptureCore), "Window Capture Mod", "1.0.0", "Earth🐾")]
[assembly: MelonGame("EmuVR", "EmuVR")]
[assembly: HarmonyDontPatchAll] // We will patch manually. 

namespace WIGUx.Modules.WindowCaptureModule
{
    /// <summary>
    /// Manages the P/Invoke and texture creation for a SINGLE window capture instance.
    /// This component is added and removed dynamically by the WindowCaptureHook.
    /// </summary>
    public class WindowCaptureInstance : MonoBehaviour
    {
        private GameSystem gameSystem;
        private ScreenController screenController;
        private ScreenReceiver screenReceiver;
        private Retroarch retroarch;
        private Texture originalEmissionTexture;
        private Color originalEmissionColor;

        private IntPtr windowHandle = IntPtr.Zero;
        private Texture2D windowTexture;
        private Coroutine captureCoroutine;
        private RenderTexture flippedTexture;
        public Process gameProcess;
        private bool isCleaningUp = false;
        private bool hasGameInitially = false; // New flag to track initial state
        public string batPath;
        public string windowIdentifier;

        void Awake()
        {
            gameSystem = GetComponent<GameSystem>();
            if (gameSystem == null)
            {
                CaptureCore.Logger.Error("[CaptureCore] Could not find GameSystem component. Self-destructing.");
                Destroy(this);
                return;
            }
            // GameSystem can have an embedded screen or be connected to an external one.
            // We need to get the correct ScreenController instance.
            if (gameSystem.IsUsingEmbeddedScreen)
                screenController = GetComponent<ScreenController>();
            else if (gameSystem.Screen != null)
                screenController = gameSystem.Screen.GetComponent<ScreenController>();

            if (screenController != null)
            {
                screenReceiver = screenController.GetComponent<ScreenReceiver>();
            }

            retroarch = GetComponent<Retroarch>();
            CaptureCore.Logger.Msg($"[CaptureCore] Instance created for {gameObject.name}.");
        }

        void Start()
        {
            TakeOverScreens();
            if (screenController != null)
            {
                // Tell the screen controller that we are providing a texture.
                // This prevents it from trying to show the "TV noise" or "off" texture.
                screenController.receivingTexture = true;
            }
            captureCoroutine = StartCoroutine(LaunchAndCapture());
        }

        void Update()
        {
            if (isCleaningUp) return; // Prevent any logic from running while cleaning up.

            if (!hasGameInitially && gameSystem.Game != null)
            {
                hasGameInitially = true;
            }

            // If we started with a game and now we don't, it means the cartridge was ejected.
            // This prevents the component from destroying itself during initialization.
            if (hasGameInitially && gameSystem.Game == null)
            {
                CaptureCore.Logger.Msg($"[CaptureCore] Game medium ejected from {gameObject.name}. Starting cleanup.");
                StartCleanup();
                return; // Exit early to prevent other checks
            }

            // If the GameSystem's retroarch state is false, but we are still running, it means the system was powered off.
            if (!gameSystem.retroarchIsRunning && captureCoroutine != null)
            {
                CaptureCore.Logger.Msg($"[CaptureCore] GameSystem for {gameObject.name} has been powered off. Starting cleanup.");
                StartCleanup();
            }
        }

        private void TakeOverScreens()
        {
            if (screenController == null || screenController.screenMaterial == null)
            {
                CaptureCore.Logger.Warning($"[CaptureCore] No screens found on {gameObject.name} to display capture.");
                return;
            }

            // Store the original texture and color from the ScreenController's material.
            originalEmissionTexture = screenController.screenMaterial.GetTexture("_EmissionMap");
            originalEmissionColor = screenController.screenMaterial.GetColor("_EmissionColor");
        }

        private void ApplyTextureToScreens(Texture texture)
        {
            if (screenController == null || screenController.screenMaterial == null) return;

            // We now directly modify the material instance that ScreenController owns.
            // This preserves the connection for other game systems like Retroarch.
            screenController.screenMaterial.SetTexture("_EmissionMap", texture);
            screenController.screenMaterial.SetColor("_EmissionColor", Color.white);
            screenController.screenMaterial.EnableKeyword("_EMISSION");

            // The screen shader uses a custom "_ScreenStretch" property for scaling.
            // Setting it to a value slightly larger than 1 (like the game's overscan)
            // ensures the texture stretches to fill the entire screen area.
            screenController.screenMaterial.SetVector("_ScreenStretch", new Vector2(1.02f, 1.02f));
        }

        void OnDestroy()
        {
            // The primary cleanup is now handled by CleanupAndDestroy().
            // OnDestroy is now just a final safety net.
            if (!isCleaningUp)
            {
                // This might be called if the GameObject is destroyed unexpectedly.
                // We should still try to clean up the process.
                KillGameProcess();
            }
        }

        private void KillGameProcess()
        {
            // Ensure the game system knows the game is no longer running.
            if (retroarch != null)
            {
                // If we were the controlled system, clear it.
                if (GameSystem.ControlledSystem == gameSystem && gameSystem != null)
                {
                    CaptureCore.Logger.Msg("[CaptureCore] Clearing controlled system.");
                    GameSystem.ControlledSystem = null;
                }

                retroarch.isRunning = false;
            }
            if (gameSystem != null && gameSystem.isServer && gameSystem.retroarchIsRunning)
            {
                gameSystem.NetworkretroarchIsRunning = false;
            }

            // Close the game process we launched
            try
            {
                if (gameProcess != null && !gameProcess.HasExited)
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] Closing process {gameProcess.ProcessName}.");
                    gameProcess.CloseMainWindow();
                    gameProcess.Kill();
                }
            }
            catch (Exception e)
            {
                CaptureCore.Logger.Warning($"[CaptureCore] Could not close game process: {e.Message}");
            }
        }

        private void StartCleanup()
        {
            if (isCleaningUp) return;
            isCleaningUp = true;
            StartCoroutine(CleanupAndDestroy());
        }

        private IEnumerator CleanupAndDestroy()
        {
            CaptureCore.Logger.Msg($"[CaptureCore] Running cleanup for {gameObject.name}.");
            KillGameProcess();
            // Restore the original texture and color to the ScreenController's material.
            if (screenController != null)
            {
                // This is the game's built-in, reliable way to reset the screen.
                screenController.EnableOffTexture();
            }

            yield return null; // Wait a frame to ensure state changes propagate.

            if (windowTexture != null)
            {
                Destroy(windowTexture);
            }
            if (flippedTexture != null)
            {
                RenderTexture.ReleaseTemporary(flippedTexture);
                flippedTexture = null;
            }

            Destroy(this);
        }
        #region Window Capture
        private IEnumerator LaunchAndCapture()
        {
            // 2. Launch the game using the .bat file
            try
            {
                string fullBatPath = Path.GetFullPath(batPath);
                string gameDirectory = Path.GetDirectoryName(fullBatPath);
                CaptureCore.Logger.Msg($"[CaptureCore] Executing: {fullBatPath}");
                ProcessStartInfo startInfo = new ProcessStartInfo(fullBatPath)
                {
                    WorkingDirectory = gameDirectory,
                    UseShellExecute = true // UseShellExecute is often better for .bat files
                };
                Process.Start(startInfo); // Launch the batch file, but don't store this cmd process.
            }
            catch (Exception e)
            {
                CaptureCore.Logger.Error($"[CaptureCore] Failed to launch .bat file: {e.Message}");
                Destroy(this);
                yield break;
            }

            // Give the actual game a moment to start up after the batch file runs.
            // This helps prevent us from capturing the cmd window by mistake.
            for (int i = 0; i < 4; i++)
            {
                yield return new WaitForSeconds(0.25f);
            }

            // 3. Find the window using the identifier from the .win file
            CaptureCore.Logger.Msg($"[CaptureCore] Searching for process/window with identifier: '{windowIdentifier}'");

            // 4. Main capture loop
            while (this != null && !this.gameObject.Equals(null)) // Loop as long as this component exists
            {
                // If we don't have a window handle, try to find one.
                if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
                {
                    if (windowHandle != IntPtr.Zero)
                    {
                        CaptureCore.Logger.Msg("[CaptureCore] Window handle became invalid. Searching for a new one...");
                        windowHandle = IntPtr.Zero;
                    }

                    // Method 1: Search by Process Name (more reliable)
                    string processName = windowIdentifier;
                    if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        processName = Path.GetFileNameWithoutExtension(processName);
                    }

                    Process[] processes = Process.GetProcessesByName(processName);
                    if (processes.Length > 0)
                    {
                        // Find the first process that isn't cmd.exe and has a window.
                        gameProcess = Array.Find(processes, p => p.ProcessName != "cmd" && p.MainWindowHandle != IntPtr.Zero);

                        if (gameProcess != null)
                        {
                            windowHandle = gameProcess.MainWindowHandle;
                            CaptureCore.Logger.Msg($"[CaptureCore] Found process '{processName}' by name. Handle: {windowHandle}");
                        }
                    }

                    // Method 2: Search by Window Title (fallback)
                    if (windowHandle == IntPtr.Zero)
                    {
                        windowHandle = FindWindow(null, windowIdentifier);
                    }

                    if (windowHandle != IntPtr.Zero)
                    {
                         CaptureCore.Logger.Msg($"[CaptureCore] Acquired window handle: {windowHandle}. Resuming capture.");
                    }
                }

                // If we have a valid handle, capture it.
                if (windowHandle != IntPtr.Zero && IsWindow(windowHandle))
                {
                    CaptureAndBlit(windowHandle);
                }

                // If the underlying process has exited, we should stop trying.
                if (gameProcess != null && gameProcess.HasExited)
                {
                    CaptureCore.Logger.Msg("[CaptureCore] Target process has exited. Stopping capture loop.");
                    break; // Exit the while loop
                }

                yield return null; // Capture at game's framerate (as fast as possible)
            }

            // If the loop breaks, it means the window was closed.
            // Destroy this component, which will trigger OnDestroy() for full cleanup.
            StartCleanup();
        }

        private void CaptureAndBlit(IntPtr handle)
        {
            if (!GetClientRect(handle, out RECT clientRect)) return;

            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;

            if (width <= 0 || height <= 0) return;

            if (windowTexture == null || windowTexture.width != width || windowTexture.height != height)
            {
                if (windowTexture != null) Destroy(windowTexture);
                // Create the source texture as LINEAR (linear=true). This "tricks" Unity into not
                // applying its own gamma correction, allowing the screen shader to do it.
                windowTexture = new Texture2D(width, height, TextureFormat.BGRA32, false, true);

                if (flippedTexture != null) RenderTexture.ReleaseTemporary(flippedTexture);
                // The destination texture must also be LINEAR to prevent conversion during the blit.
                flippedTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
                flippedTexture.filterMode = FilterMode.Bilinear;
                // Apply the final render texture to the screens. This only needs to be done
                // when the texture is resized.
                ApplyTextureToScreens(flippedTexture);
            }

            IntPtr hdcSrc = GetDC(handle); // Use GetDC for the client area
            IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcSrc, width, height);
            IntPtr hOld = SelectObject(hdcDest, hBitmap);

            // You can try swapping between BitBlt and PrintWindow.
            // PrintWindow can sometimes be faster or capture windows that BitBlt cannot.
            // The '2' flag (PW_RENDERFULLCONTENT) is undocumented but often needed for DirectX/OpenGL windows. It's worth testing both.
            //PrintWindow(handle, hdcDest, 2);
            // Using CAPTUREBLT can sometimes reduce latency by bypassing DWM composition.
            BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, SRCCOPY | CAPTUREBLT);
            SelectObject(hdcDest, hOld);
            ReleaseDC(handle, hdcSrc);

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.biSize = 40;
            bmi.biWidth = width;
            bmi.biHeight = -height; // Negative height to get a top-down DIB
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            bmi.biCompression = 0; // BI_RGB

            byte[] bmpBytes = new byte[width * height * 4];
            GetDIBits(hdcDest, hBitmap, 0, (uint)height, bmpBytes, ref bmi, 0);

            DeleteDC(hdcDest);
            DeleteObject(hBitmap);

            windowTexture.LoadRawTextureData(bmpBytes);
            windowTexture.Apply();

            // Flip the texture vertically using a blit.
            Graphics.Blit(windowTexture, flippedTexture, new Vector2(1, -1), new Vector2(0, 1));
        }

        #endregion

        #region P/Invoke Win32 API
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        const int SRCCOPY = 0x00CC0020;
        const int CAPTUREBLT = 0x40000000;
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd); // Changed from GetWindowDC
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }
        #endregion
    }
    
    /// <summary>
    /// This is now a MelonMod, which is the main entry point for our code.
    /// It will inject the hooks needed for the capture functionality to work.
    /// </summary>
    public class CaptureCore : MelonMod
    {
        public static MelonLogger.Instance Logger { get; private set; }
        private HarmonyLib.Harmony harmony;

        public override void OnInitializeMelon()
        {
            Logger = LoggerInstance;
            harmony = new HarmonyLib.Harmony("com.yourname.windowcapture");
            
            // Patch 1: Intercept the game's internal power-on logic.
            var internalPowerOriginal = typeof(GameSystemState).GetMethod("InternalPower", BindingFlags.NonPublic | BindingFlags.Instance);
            var internalPowerPrefix = typeof(CaptureCore).GetMethod(nameof(OnInternalPowerPrefix));
            harmony.Patch(internalPowerOriginal, new HarmonyMethod(internalPowerPrefix));

            // Patch 2: Correctly handle re-focusing for our custom systems.
            var updateOriginal = typeof(GameSystem).GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var updatePrefix = typeof(CaptureCore).GetMethod(nameof(OnGameSystemUpdatePrefix));
            harmony.Patch(updateOriginal, new HarmonyMethod(updatePrefix));

            // Patch 3: Prevent Retroarch.Open from launching for our custom games.
            var retroarchOpenOriginal = typeof(Retroarch).GetMethod("Open", BindingFlags.Public | BindingFlags.Instance);
            var retroarchOpenPrefix = typeof(CaptureCore).GetMethod(nameof(OnRetroarchOpenPrefix));
            harmony.Patch(retroarchOpenOriginal, new HarmonyMethod(retroarchOpenPrefix));
        }

        public static bool OnGameSystemUpdatePrefix(GameSystem __instance, LongPressControl ___longPressControl)
        {
            var captureInstance = __instance.GetComponent<WindowCaptureInstance>();
            if (captureInstance == null)
            {
                return true; // Not our system, run the original Update method.
            }

            if (___longPressControl.GetLongPress(Button.EngageSystem, LongPressMode.Canceled) && (bool)(UnityEngine.Object)__instance.Screen)
            {
                GameSystem.ControlledSystem = __instance;

                var screenController = __instance.Screen.GetComponent<ScreenController>();
                if (screenController != null && !screenController.tvOn)
                {
                    screenController.TVToggle(true);
                }

                return false; // We've handled the re-focus, so we skip the original Update method for this frame.
            }

            return true;
        }

        public static bool OnRetroarchOpenPrefix(Retroarch __instance)
        {
            var gameSystem = __instance.GetComponent<GameSystem>();
            if (gameSystem != null && gameSystem.GetComponent<WindowCaptureInstance>() != null)
            {
                Logger.Msg("[CaptureCore] Retroarch.Open() intercepted. Preventing RetroArch launch.");
                // This is the key: We tell the retroarch component that we are "running"
                // so that the game doesn't think the launch failed and turn itself off.
                __instance.isRunning = true;
                return false; // Skip the original Open() method.
            }

            return true; // Not our system, let RetroArch launch normally.
        }

        public static bool OnInternalPowerPrefix(GameSystemState __instance, bool on)
        {
            Game game = __instance.gameSystem.Game;
            if (game == null || string.IsNullOrEmpty(game.path)) return true;

            string gameDirectory = Path.GetDirectoryName(game.path);
            string gameNameWithoutExt = Path.GetFileNameWithoutExtension(game.path);
            string winFilePath = Path.Combine(gameDirectory, gameNameWithoutExt + ".win");
            string batFilePath = Path.Combine(gameDirectory, gameNameWithoutExt + ".bat");
            
            if (!File.Exists(winFilePath) || !File.Exists(batFilePath))
            {
                return true; // Not our game, run original method.
            }
            
            if (on)
            {
                if (__instance.gameSystem.gameObject.GetComponent<WindowCaptureInstance>() == null)
                {
                    Logger.Msg($"[CaptureCore] Intercepting Power-On for '{game.name}'.");
                    string windowIdentifier = File.ReadAllText(winFilePath).Trim();
                    if (string.IsNullOrEmpty(windowIdentifier))
                    {
                        Logger.Error($"[CaptureCore] .win file for '{game.name}' is empty.");
                        return true; // Let original method run and likely fail.
                    }

                    // Add our component to launch the capture instead.
                    var instance = __instance.gameSystem.gameObject.AddComponent<WindowCaptureInstance>();
                    instance.batPath = batFilePath;
                    instance.windowIdentifier = windowIdentifier;
                }
            }
            else
            {
                var instance = __instance.gameSystem.GetComponent<WindowCaptureInstance>();
                if (instance != null) UnityEngine.Object.Destroy(instance);
            }

            // Let the original InternalPower method run. It will handle all the state changes,
            // but our new patch on Retroarch.Open() will stop the emulator from actually launching.
            return true;
        }
    }
}

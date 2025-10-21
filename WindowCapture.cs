using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;
using System.Diagnostics;
using MelonLoader;
using WIGU;
using System.Linq;
using UnityEngine.Rendering;


[assembly: MelonInfo(typeof(WIGUx.Modules.WindowCaptureModule.CaptureCore), "Window Capture Mod", "1.4.0", "Earth🐾")]
[assembly: MelonGame("EmuVR", "EmuVR")]
[assembly: HarmonyDontPatchAll] // We will patch manually. 

namespace WIGUx.Modules.WindowCaptureModule
{
    /// <summary>
    /// Manages the window capture process for a single game system OR a test screen.
    /// </summary>
    public class WindowCaptureInstance : MonoBehaviour
    {
        // --- Shared Memory and Capture fields ---
        private MemoryMappedFile memoryFile;
        private MemoryMappedViewAccessor accessor;

        // --- EmuVR component fields (for game capture only) ---
        private GameSystem gameSystem;
        private ScreenController screenController;
        private ScreenReceiver screenReceiver;
        private Retroarch retroarch;
        private BoxCollider screenCollider;

        // --- Texture and Process Management ---
        private Texture2D windowTexture;
        private Coroutine captureCoroutine;
        private Process gameProcess;
        private Process captureProcess;
        private bool isCleaningUp = false;
        private bool hasGameInitially = false;
        public bool isTestScreen = false;
        public string batPath;
        
        // --- Touchscreen Integration ---
        public string windowIdentifier;
        public float touchOffsetX = 0.0330f; // Adjustable offset for fingertip alignment
        public float touchOffsetY = -0.0070f; // Adjustable offset for fingertip alignment
        public float touchOffsetXLeft = -0.0370f; // Adjustable offset for fingertip alignment
        public float touchOffsetYLeft = -0.0110f;
        public string gridConfiguration;
        private IntPtr sharedTextureHandle = IntPtr.Zero; 

        // --- Multitouch State Management ---
        // _persistentContacts is the source of truth for what the OS thinks is down.
        private Dictionary<uint, WinTouch.POINTER_TOUCH_INFO> _persistentContacts = new Dictionary<uint, WinTouch.POINTER_TOUCH_INFO>();
        // activeFrameContacts is a temporary dictionary to gather events from a single physics frame.
        private Dictionary<uint, WinTouch.POINTER_TOUCH_INFO> _activeFrameContacts = new Dictionary<uint, WinTouch.POINTER_TOUCH_INFO>();
        private uint nextTouchId = 0;
        private HashSet<uint> touchesToRemove = new HashSet<uint>();

        // --- Definitive Light Color Fix ---
        private RenderTexture lightColorTexture;
        private AsyncGPUReadbackRequest lightColorRequest;
        private bool isLightColorRequestInFlight = false;

        // --- Reflection Cache ---
        private FieldInfo lightScaleField;
        private FieldInfo screenAspectRatioField;
        private FieldInfo overscanField;

        // --- Thread-safe data transfer from coroutine to Update() ---
        private volatile bool newFrameDataAvailable = false;
        private uint nextWidth, nextHeight;

        // --- P/Invoke for our native RenderPlugin.dll ---
        private const string PluginName = "RenderPlugin";

        [DllImport(PluginName)]
        private static extern IntPtr GetRenderEventFunc();

        [DllImport(PluginName)]
        private static extern void SetSharedHandle(IntPtr handle);

        [DllImport(PluginName)]
        private static extern void SetTextureFromUnity(IntPtr texturePtr);

        [DllImport(PluginName)]
        private static extern void SetLogFunction(IntPtr fp);

        delegate void DebugLogDelegate(string s);

        // --- P/Invoke for Window Rect ---
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        // DWMWA_EXTENDED_FRAME_BOUNDS gets the window rect in physical pixels, ignoring DPI scaling.
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        private IntPtr capturedHwnd = IntPtr.Zero;

        void Awake()
        {
            InitializePluginLogging();

            // This logic is common to both modes if a screen exists.
            if (screenController != null)
            {
                screenReceiver = screenController.GetComponent<ScreenReceiver>();
                var childColliders = screenController.GetComponentsInChildren<BoxCollider>();
                screenCollider = childColliders.FirstOrDefault(c => c.GetComponent<MeshRenderer>() != null);
                if (screenCollider == null && childColliders.Length > 0)
                    screenCollider = childColliders[0];
            }
            
            lightColorTexture = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32);

            if (screenController != null)
            {
                lightScaleField = typeof(ScreenController).GetField("lightScale", BindingFlags.NonPublic | BindingFlags.Instance);
                screenAspectRatioField = typeof(ScreenController).GetField("screenAspectRatio", BindingFlags.NonPublic | BindingFlags.Instance);
                overscanField = typeof(ScreenController).GetField("overscan", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            CaptureCore.Logger.Msg($"[CaptureCore] Instance created for {gameObject.name}.");
        }

        public void InitializeForTestScreen()
        {
            isTestScreen = true;
            // Test Screen: Only needs a collider and a basic material.
            screenCollider = GetComponent<BoxCollider>();
            CaptureCore.Logger.Msg($"[CaptureCore] Test screen instance prepared for {gameObject.name}.");

        }

        private void InitializePluginLogging()
        {
            DebugLogDelegate logDelegate = (s) => { CaptureCore.Logger.Msg(s); };
            IntPtr pLogDelegate = Marshal.GetFunctionPointerForDelegate(logDelegate);
            SetLogFunction(pLogDelegate);
            GCHandle.Alloc(logDelegate);
        }

        void Start()
        {
            if (isTestScreen)
            {
                // Test Screen Initialization
                var renderer = GetComponent<Renderer>();
                if (renderer.material == null)
                {
                    CaptureCore.Logger.Error("[CaptureCore] Test screen renderer has no material. Cannot proceed.");
                    Destroy(this);
                }
                InitializeTouch();
                captureCoroutine = StartCoroutine(LaunchDesktopCaptureLoop());
            }
            else
            {
                // Game Capture Initialization
                gameSystem = GetComponent<GameSystem>();
                if (gameSystem == null)
                {
                    CaptureCore.Logger.Error("[CaptureCore] Could not find GameSystem component. Self-destructing.");
                    Destroy(this);
                    return;
                }

                if (gameSystem.IsUsingEmbeddedScreen)
                    screenController = GetComponent<ScreenController>();
                else if (gameSystem.Screen != null)
                    screenController = gameSystem.Screen.GetComponent<ScreenController>();

                retroarch = GetComponent<Retroarch>();

                InitializeTouch();

                TakeOverScreen();
                captureCoroutine = StartCoroutine(LaunchAndCaptureLoop());
            }
        }

        void Update()
        {
            if (isCleaningUp) return;

            if (!isTestScreen && isLightColorRequestInFlight && lightColorRequest.done)
            {
                if (!lightColorRequest.hasError && screenController != null)
                {
                    Color averageColor = lightColorRequest.GetData<Color32>()[0];
                    screenController.LightColor = averageColor;
                    float lightScale = (float)lightScaleField.GetValue(screenController);
                    screenController.LightIntensity = averageColor.grayscale * lightScale;
                }
                isLightColorRequestInFlight = false;
            }

            if (!isTestScreen && gameSystem != null)
            {
                if (gameSystem.Screen != null && screenController?.gameObject != gameSystem.Screen)
                {
                    if (screenController != null) screenController.EnableOffTexture();
                    screenController = gameSystem.Screen.GetComponent<ScreenController>();
                    if (screenController != null)
                    {
                        screenReceiver = screenController.GetComponent<ScreenReceiver>();
                        screenCollider = screenController.GetComponent<BoxCollider>();
                        TakeOverScreen();
                        if (windowTexture != null)
                        {
                            ApplyTextureToScreens(windowTexture);
                            UpdateScreenParameters(nextWidth, nextHeight);
                        }
                    }
                }
                else if (gameSystem.Screen == null && screenController != null)
                {
                    screenController.EnableOffTexture();
                    screenController = null;
                    screenReceiver = null;
                }

                if (!hasGameInitially && gameSystem.Game != null) hasGameInitially = true;

                if (hasGameInitially && gameSystem.Game == null)
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] Game medium ejected from {gameObject.name}. Starting cleanup.");
                    StartCleanup();
                    return; 
                }

                if (hasGameInitially && !gameSystem.retroarchIsRunning && !isCleaningUp)
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] GameSystem for {gameObject.name} has been powered off. Starting cleanup.");
                    StartCleanup();
                }
            }

            if (newFrameDataAvailable)
            {
                newFrameDataAvailable = false;
                ProcessFrame();
            }

            float adjustmentAmount = 0.001f;
            bool modifier = Input.GetKey(KeyCode.RightShift);

            if (modifier && Input.GetKeyDown(KeyCode.UpArrow))
            {
                touchOffsetY += adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Touch Offset Updated: X={touchOffsetX:F4}, Y={touchOffsetY:F4}");
            }
            if (modifier && Input.GetKeyDown(KeyCode.DownArrow))
            {
                touchOffsetY -= adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Touch Offset Updated: X={touchOffsetX:F4}, Y={touchOffsetY:F4}");
            }
            if (modifier && Input.GetKeyDown(KeyCode.RightArrow))
            {
                touchOffsetX += adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Touch Offset Updated: X={touchOffsetX:F4}, Y={touchOffsetY:F4}");
            }
            if (modifier && Input.GetKeyDown(KeyCode.LeftArrow))
            {
                touchOffsetX -= adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Touch Offset Updated: X={touchOffsetX:F4}, Y={touchOffsetY:F4}");
            }
            if (modifier && Input.GetKeyDown(KeyCode.PageUp))
            {
                touchOffsetX = 0f;
                touchOffsetY = 0f;
                CaptureCore.Logger.Msg($"[CaptureCore] Touch Offset Reset.");
            }
            if (modifier && Input.GetKeyDown(KeyCode.PageDown))
            {
                CaptureCore.Logger.Msg($"[CaptureCore] Current Touch Offset: X={touchOffsetX:F4}, Y={touchOffsetY:F4}");
            }

            // --- Real-time Touch Offset Adjustment for Left Hand (Left Shift) ---
            bool leftModifier = Input.GetKey(KeyCode.RightControl);
            if (leftModifier && Input.GetKeyDown(KeyCode.UpArrow))
            {
                touchOffsetYLeft += adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Left Touch Offset Updated: X={touchOffsetXLeft:F4}, Y={touchOffsetYLeft:F4}");
            }
            if (leftModifier && Input.GetKeyDown(KeyCode.DownArrow))
            {
                touchOffsetYLeft -= adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Left Touch Offset Updated: X={touchOffsetXLeft:F4}, Y={touchOffsetYLeft:F4}");
            }
            if (leftModifier && Input.GetKeyDown(KeyCode.RightArrow))
            {
                touchOffsetXLeft += adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Left Touch Offset Updated: X={touchOffsetXLeft:F4}, Y={touchOffsetYLeft:F4}");
            }
            if (leftModifier && Input.GetKeyDown(KeyCode.LeftArrow))
            {
                touchOffsetXLeft -= adjustmentAmount;
                CaptureCore.Logger.Msg($"[CaptureCore] Left Touch Offset Updated: X={touchOffsetXLeft:F4}, Y={touchOffsetYLeft:F4}");
            }
        }

        void FixedUpdate()
        {
            // Inject touch points in FixedUpdate to ensure it runs after all physics events (OnCollision, etc.)
            InjectAllTouchPoints();
        }

        private void TakeOverScreen()
        {
            if (screenController == null) return;
            screenController.receivingTexture = true;
            screenController.NetworktvOn = true;
        }

        private void ApplyTextureToScreens(Texture texture)
        {
            if (isTestScreen)
            {
                GetComponent<Renderer>().material.mainTexture = texture;
            }
            else if (screenController != null && screenController.screenMaterial != null)
            {
                screenController.screenMaterial.SetTexture("_EmissionMap", texture);
            }
        }

        void OnDestroy()
        {
            if (!isCleaningUp)
            {
                StartCleanup();
            }
        }

        private void KillChildProcesses()
        {
            if (!isTestScreen && gameSystem != null)
            {
                if (retroarch != null) 
                {
                    if (GameSystem.ControlledSystem == gameSystem) GameSystem.ControlledSystem = null;
                    retroarch.isRunning = false;
                }
                if (gameSystem.isServer && gameSystem.retroarchIsRunning)
                    gameSystem.NetworkretroarchIsRunning = false;
            }

            try
            {
                if (captureProcess != null && !captureProcess.HasExited)
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] Closing capture process {captureProcess.ProcessName}.");
                    captureProcess.CloseMainWindow();
                    captureProcess.Kill();
                }
            }
            catch (Exception e) { CaptureCore.Logger.Warning($"[CaptureCore] Could not close capture process: {e.Message}"); }

            try
            {
                if (gameProcess != null && !gameProcess.HasExited)
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] Closing process {gameProcess.ProcessName}.");
                    gameProcess.Kill();
                }
            }
            catch (Exception e) { CaptureCore.Logger.Warning($"[CaptureCore] Could not close game process: {e.Message}"); }
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
            KillChildProcesses();

            if (screenController != null) screenController.EnableOffTexture();

            yield return null;

            accessor?.Dispose();
            memoryFile?.Dispose();

            if (lightColorTexture != null) Destroy(lightColorTexture);

            Destroy(this);
        }

        private IEnumerator ConnectToSharedMemory(string sharedMemoryName)
        {
            accessor?.Dispose();
            memoryFile?.Dispose(); 
            accessor = null;
            memoryFile = null;

            CaptureCore.Logger.Msg($"[CaptureCore] Attempting to connect to Shared Memory: '{sharedMemoryName}'");

            yield return new WaitUntil(() => {
                if (captureProcess == null || captureProcess.HasExited) return true;
                if (isCleaningUp) return true;
                try
                {
                    memoryFile = MemoryMappedFile.OpenExisting(sharedMemoryName, MemoryMappedFileRights.ReadWrite); 
                    return true;
                }
                catch (FileNotFoundException) { return false; }
                catch (Exception e)
                {
                    CaptureCore.Logger.Error($"[CaptureCore] Error trying to open memory file: {e.Message}");
                    return true;
                }
            });

            if (memoryFile != null)
            {
                CaptureCore.Logger.Msg($"[CaptureCore] Successfully connected to Shared Memory '{sharedMemoryName}'.");
                accessor = memoryFile.CreateViewAccessor(0, 24, MemoryMappedFileAccess.Read); // W+H+Handle+HWND
            }
            else
            {
                if (!isCleaningUp) CaptureCore.Logger.Error($"[CaptureCore] Failed to connect to Shared Memory '{sharedMemoryName}'. Capture process might have failed to start.");
            }
        }

        private IEnumerator LaunchDesktopCaptureLoop()
        {
            string sharedMemoryName = "EmuVR_DesktopCapture_SHM";
            try
            {
                string userDataPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserData");
                string captureExePath = Path.Combine(userDataPath, "WindowCapture", "GraphicsCapture.exe");
                string launchArguments = $"--desktop --memname \"{sharedMemoryName}\"";

                CaptureCore.Logger.Msg($"[CaptureCore] Launching desktop capture: {captureExePath}");
                CaptureCore.Logger.Msg($"[CaptureCore] With arguments: {launchArguments}");

                captureProcess = Process.Start(new ProcessStartInfo(captureExePath, launchArguments)
                {
                    WorkingDirectory = Path.GetDirectoryName(captureExePath),
                    UseShellExecute = true
                });

                if (captureProcess != null) CaptureCore.RegisterCaptureProcess(captureProcess);
            }
            catch (Exception e)
            {
                CaptureCore.Logger.Error($"[CaptureCore] Failed to launch GraphicsCapture.exe for desktop: {e.Message}");
                StartCleanup();
                yield break;
            }

            while (this != null && !this.gameObject.Equals(null))
            {
                if (isCleaningUp) yield break;
                if (accessor == null)
                {
                    yield return StartCoroutine(ConnectToSharedMemory(sharedMemoryName));
                    if (accessor == null) continue;
                }
                CaptureAndApply();
                yield return null;
            }
            StartCleanup();
        }

        private IEnumerator LaunchAndCaptureLoop()
        {
            bool useHook = false;
            string sharedMemoryName;

            try
            {
                string userDataPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UserData");
                string captureExePath = Path.Combine(userDataPath, "WindowCapture", "GraphicsCapture.exe");

                string[] winFileLines = windowIdentifier.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string processName = winFileLines.Length > 0 ? winFileLines[0].Trim() : windowIdentifier.Trim();
                useHook = winFileLines.Length > 1 && winFileLines[1].Trim().ToUpper() == "HOOK";

                sharedMemoryName = $"{Path.GetFileNameWithoutExtension(processName)}_{gameSystem.GetInstanceID()}_SHM";
                string launchArguments = $"--target \"{processName}\" --memname \"{sharedMemoryName}\"";

                if (useHook) launchArguments += " --hook";

                CaptureCore.Logger.Msg($"[CaptureCore] Launching capture program: {captureExePath}");
                CaptureCore.Logger.Msg($"[CaptureCore] With arguments: {launchArguments}");

                captureProcess = Process.Start(new ProcessStartInfo(captureExePath, launchArguments)
                {
                    WorkingDirectory = Path.GetDirectoryName(captureExePath),
                    UseShellExecute = true
                });

                if (captureProcess != null) CaptureCore.RegisterCaptureProcess(captureProcess);
            }
            catch (Exception e)
            {
                CaptureCore.Logger.Error($"[CaptureCore] Failed to launch GraphicsCapture.exe: {e.Message}");
                StartCleanup();
                yield break;
            }

            if (!useHook)
            {
                try
                {
                    CaptureCore.Logger.Msg($"[CaptureCore] Executing game launcher: {batPath}");
                    Process.Start(new ProcessStartInfo(batPath)
                    {
                        WorkingDirectory = Path.GetDirectoryName(batPath),
                        UseShellExecute = true
                    });
                }
                catch (Exception e)
                {
                    CaptureCore.Logger.Error($"[CaptureCore] Failed to launch game via .bat file: {e.Message}");
                    StartCleanup();
                    yield break;
                }
            }

            string[] winFileLinesForName = windowIdentifier.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string gameProcessName = Path.GetFileNameWithoutExtension(winFileLinesForName.Length > 0 ? winFileLinesForName[0].Trim() : windowIdentifier.Trim());
            gameProcess = null;
            CaptureCore.Logger.Msg($"[CaptureCore] Searching for game process: '{gameProcessName}'...");

            for (int i = 0; i < 30; i++)
            {
                var processes = Process.GetProcessesByName(gameProcessName);
                if (processes.Length > 0)
                {
                    gameProcess = processes[0];
                    CaptureCore.Logger.Msg($"[CaptureCore] Found game process with ID: {gameProcess.Id}");
                    break;
                }
                yield return new WaitForSeconds(0.5f);
            }

            if (gameProcess == null)
            {
                CaptureCore.Logger.Error($"[CaptureCore] Timed out waiting for game process '{gameProcessName}' to start. Aborting.");
                StartCleanup();
                yield break;
            }

            while (this != null && !this.gameObject.Equals(null))
            {
                if (isCleaningUp) yield break;
                if (accessor == null)
                {
                    yield return StartCoroutine(ConnectToSharedMemory(sharedMemoryName));
                    if (accessor == null)
                    {
                        if (gameProcess == null || gameProcess.HasExited)
                        {
                            CaptureCore.Logger.Msg("[CaptureCore] Game process has exited while trying to connect to memory. Aborting.");
                            break;
                        }
                        CaptureCore.Logger.Warning($"[CaptureCore] Failed to connect to shared memory. Retrying...");
                        yield return new WaitForSeconds(1.0f);
                        continue;
                    }
                }

                CaptureAndApply();

                if (gameProcess != null && !isCleaningUp && gameProcess.HasExited)
                {
                    CaptureCore.Logger.Msg("[CaptureCore] Target process has exited. Stopping capture loop.");
                    break;
                }
                yield return null;
            }
            StartCleanup();
        }

        private void CaptureAndApply()
        {
            if (accessor == null) return;
            uint width = 0, height = 0;
            IntPtr newHandle = IntPtr.Zero, newHwnd = IntPtr.Zero;

            try
            {
                width = accessor.ReadUInt32(0);
                height = accessor.ReadUInt32(4);
                newHandle = (IntPtr)accessor.ReadInt64(8);
                newHwnd = (IntPtr)accessor.ReadInt64(16);
            }
            catch (Exception e)
            {
                CaptureCore.Logger.Warning($"[CaptureCore] Shared memory read failed: {e.Message}");
                accessor?.Dispose();
                memoryFile?.Dispose();
                accessor = null;
                memoryFile = null;
                return; 
            }

            if (newHwnd != IntPtr.Zero && newHwnd != capturedHwnd) capturedHwnd = newHwnd;

            nextWidth = width;
            nextHeight = height;
            sharedTextureHandle = newHandle;
            newFrameDataAvailable = true;
        }

        private void ProcessFrame()
        {
            CheckAndResizeTexture(nextWidth, nextHeight);
            if (windowTexture == null) return;

            if (!isTestScreen && screenController != null && screenController.tvOn && !isLightColorRequestInFlight)
            {
                RenderTexture[] mipRTs = {
                    RenderTexture.GetTemporary(windowTexture.width / 2, windowTexture.height / 2, 0, RenderTextureFormat.Default),
                    RenderTexture.GetTemporary(windowTexture.width / 4, windowTexture.height / 4, 0, RenderTextureFormat.Default)
                };
                Graphics.Blit(windowTexture, mipRTs[0]);
                for (int i = 0; i < 5; i++) Graphics.Blit(mipRTs[i % 2], mipRTs[(i + 1) % 2]);
                Graphics.Blit(mipRTs[0], lightColorTexture);
                RenderTexture.ReleaseTemporary(mipRTs[0]);
                RenderTexture.ReleaseTemporary(mipRTs[1]);
                lightColorRequest = AsyncGPUReadback.Request(lightColorTexture);
                isLightColorRequestInFlight = true;
            }

            if (sharedTextureHandle != IntPtr.Zero) SetSharedHandle(sharedTextureHandle);
            GL.IssuePluginEvent(GetRenderEventFunc(), 1);
        }

        private void CheckAndResizeTexture(uint width, uint height)
        {
            if (width <= 0 || height <= 0 || sharedTextureHandle == IntPtr.Zero) return;
            bool needsRecreation = windowTexture == null || windowTexture.width != width || windowTexture.height != height;

            if (needsRecreation)
            {
                CaptureCore.Logger.Msg($"[CaptureCore] Resizing texture to {width}x{height}.");
                if (windowTexture != null) Destroy(windowTexture);
                windowTexture = new Texture2D((int)width, (int)height, TextureFormat.BGRA32, false, true);
                windowTexture.Apply();
                SetTextureFromUnity(windowTexture.GetNativeTexturePtr());
                SetSharedHandle(sharedTextureHandle);
                CaptureCore.Logger.Msg($"[CaptureCore] Passed new texture pointer and shared handle to plugin.");
                ApplyTextureToScreens(windowTexture);
                if (!isTestScreen) 
                {
                    UpdateScreenParameters(width, height);
                }

                if (screenController != null && screenController.screenMaterial != null)
                {
                    screenController.screenMaterial.mainTextureScale = new Vector2(1, -1);
                    screenController.screenMaterial.mainTextureOffset = new Vector2(0, 1);
                } else if (isTestScreen) {
                    GetComponent<Renderer>().material.mainTextureScale = new Vector2(1, -1);
                    GetComponent<Renderer>().material.mainTextureOffset = new Vector2(0, 1);
                }
            }
        }

        private void UpdateScreenParameters(uint width, uint height)
        {
            if (screenController == null || screenController.screenMaterial == null) return;
            float screenAspectRatio = (float)screenAspectRatioField.GetValue(screenController);
            Vector2 overscan = (Vector2)overscanField.GetValue(screenController);
            float textureAspectRatio = (float)width / height;
            float aspectMultiplierRatio = textureAspectRatio / screenAspectRatio;
            Vector2 aspectRatioMultiplier = Vector2.one;
            if (screenAspectRatio > 0)
            {
                aspectRatioMultiplier.x = aspectMultiplierRatio < 1.0f ? 1.0f / aspectMultiplierRatio : 1.0f;
                aspectRatioMultiplier.y = aspectMultiplierRatio < 1.0f ? 1.0f : aspectMultiplierRatio;
            }
            screenController.screenMaterial.SetVector("_ScreenStretch", new Vector4(overscan.x * aspectRatioMultiplier.x, overscan.y * aspectRatioMultiplier.y, 0, 0));
        }

        private void InitializeTouch()
        {
            if (screenCollider == null)
            {
                CaptureCore.Logger.Warning("[CaptureCore] Touch requested, but no screen collider found. Touch will be disabled.");
                return;
            }

            CaptureCore.Logger.Msg("[CaptureCore] Initializing Touch Injection for this instance.");
            if (!WinTouch.InitializeTouchInjection(10, WinTouch.TOUCH_FEEDBACK.DEFAULT))
            {
                CaptureCore.Logger.Error("[CaptureCore] Failed to initialize touch injection.");
                return;
            }

            Physics.autoSyncTransforms = true;
            
            // Add a larger trigger collider for hover detection if it doesn't exist
            BoxCollider hoverCollider = screenCollider.gameObject.GetComponents<BoxCollider>().FirstOrDefault(c => c.isTrigger);
            if (hoverCollider == null)
            {
                hoverCollider = screenCollider.gameObject.AddComponent<BoxCollider>();
                hoverCollider.isTrigger = true;
                hoverCollider.size = new Vector3(1.1f, 1.1f, 0.4f); // Larger than the physical collider
                CaptureCore.Logger.Msg("[CaptureCore] Added hover trigger collider for touch.");
            }

            // --- NEW: Create separate interaction script for each hand ---
            var rightHand = PlayerVRSetup.RightHand?.gameObject;
            if (rightHand != null)
            {
                var touchInteraction = rightHand.AddComponent<HandTouchInteraction>();
                touchInteraction.Initialize(this, screenCollider, hoverCollider, nextTouchId++);
            }
            else
            {
                CaptureCore.Logger.Error("[TouchscreenInteraction] Could not find Player's Right Hand.");
            }

            var leftHand = PlayerVRSetup.LeftHand?.gameObject;
            if (leftHand != null)
            {
                var touchInteraction = leftHand.AddComponent<HandTouchInteraction>();
                touchInteraction.Initialize(this, screenCollider, hoverCollider, nextTouchId++);
            }
            else
            {
                CaptureCore.Logger.Error("[TouchscreenInteraction] Could not find Player's Left Hand.");
            }
        }

        public void UpdateTouchState(uint touchId, Vector3 hitPoint, bool isStartingToDraw, Transform handTransform, bool isHover = false)
        {
            string eventType = isStartingToDraw ? "DOWN" : "UPDATE";

            // --- Rotation-Aware Offset Calculation ---
            float offsetX, offsetY;
            if (touchId == 0) // Right Hand
            {
                offsetX = touchOffsetX;
                offsetY = touchOffsetY;
            }
            else // Left Hand
            {
                offsetX = touchOffsetXLeft;
                offsetY = touchOffsetYLeft;
            }

            // 1. Create a 3D offset vector in the hand's local space.
            Vector3 localOffset = new Vector3(offsetX, offsetY, 0);
            // 2. Transform this local offset into a world-space direction, accounting for hand rotation.
            Vector3 worldOffset = handTransform.TransformDirection(localOffset);
            // 3. Apply the world-space offset to the collision point.
            Vector3 adjustedHitPoint = hitPoint + worldOffset;
            // 4. Convert the adjusted point to local UV coordinates.
            Vector3 localHit = screenCollider.transform.InverseTransformPoint(adjustedHitPoint);
            float u = localHit.x + 0.5f;
            float v = localHit.y + 0.5f;

            // 3. Calculate the final pixel coordinates from the adjusted UVs.
            int touchX, touchY;
            if (isTestScreen)
            {
                touchX = (int)(u * CaptureCore.GetSystemMetrics(CaptureCore.SM_CXSCREEN));
                touchY = (int)((1 - v) * CaptureCore.GetSystemMetrics(CaptureCore.SM_CYSCREEN));
            }
            else
            {
                RECT windowRect;
                if (capturedHwnd == IntPtr.Zero || DwmGetWindowAttribute(capturedHwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out windowRect, Marshal.SizeOf(typeof(RECT))) != 0) return;
                touchX = windowRect.Left + (int)(u * (windowRect.Right - windowRect.Left));
                touchY = windowRect.Top + (int)((1 - v) * (windowRect.Bottom - windowRect.Top));
            }

            var contact = new WinTouch.POINTER_TOUCH_INFO();
            contact.pointerInfo.pointerType = WinTouch.POINTER_INPUT_TYPE.PT_TOUCH;
            contact.pointerInfo.pointerId = touchId;

            if (isHover)
            {
                contact.pointerInfo.pointerFlags = WinTouch.POINTER_FLAGS.INRANGE | WinTouch.POINTER_FLAGS.UPDATE;
            }
            else
            {
                contact.pointerInfo.pointerFlags = WinTouch.POINTER_FLAGS.INRANGE | WinTouch.POINTER_FLAGS.INCONTACT;
                if (isStartingToDraw)
                {
                    contact.pointerInfo.pointerFlags |= WinTouch.POINTER_FLAGS.DOWN;
                }
                else
                {
                    contact.pointerInfo.pointerFlags |= WinTouch.POINTER_FLAGS.UPDATE;
                }
            }

            contact.pointerInfo.ptPixelLocation.x = touchX;
            contact.pointerInfo.ptPixelLocation.y = touchY;
            contact.touchMask = WinTouch.TOUCH_MASK.CONTACTAREA | WinTouch.TOUCH_MASK.ORIENTATION | WinTouch.TOUCH_MASK.PRESSURE;
            contact.orientation = 90;
            contact.pressure = 1024;
            contact.contact.left = touchX - 2; contact.contact.right = touchX + 2;
            contact.contact.top = touchY - 2; contact.contact.bottom = touchY + 2;

            // Queue the event for this physics frame. It will be processed in FixedUpdate.
            _activeFrameContacts[touchId] = contact;
        }

        public void HandleDrawEnd(uint touchId)
        {
            // Don't do anything immediately. Just flag this touchId for removal.
            // The actual UP event will be handled in FixedUpdate.
            touchesToRemove.Add(touchId);
        }

        public void HandleHover(uint touchId, Vector3 hitPoint, Transform handTransform)
        {
            // A hover is like a draw update, but without the DOWN/INCONTACT flags.
            // We can reuse the UpdateTouchState logic but force it to be a non-drawing event.
            UpdateTouchState(touchId, hitPoint, false, handTransform, true);
        }


        private void InjectAllTouchPoints()
        {
            // 1. Merge the contacts from the current physics frame into our persistent state.
            foreach (var frameContact in _activeFrameContacts)
            {
                _persistentContacts[frameContact.Key] = frameContact.Value;
            }

            // 2. If no touches are active or pending removal, do nothing.
            if (_persistentContacts.Count == 0)
            {
                _activeFrameContacts.Clear();
                touchesToRemove.Clear();
                return;
            }

            // 3. Build the injection list from the persistent state.
            var contactsToInject = new List<WinTouch.POINTER_TOUCH_INFO>();

            foreach (var persistentContact in _persistentContacts)
            {
                var contact = persistentContact.Value;

                if (touchesToRemove.Contains(persistentContact.Key))
                {
                    // If this touch was flagged for removal, send an UP event.
                    contact.pointerInfo.pointerFlags = WinTouch.POINTER_FLAGS.UP;
                }
                else if ((contact.pointerInfo.pointerFlags & WinTouch.POINTER_FLAGS.INCONTACT) == 0)
                {
                    // This is a hover point. It should be INRANGE but not INCONTACT.
                    // The DOWN flag should also be removed if it was accidentally added.
                    contact.pointerInfo.pointerFlags &= ~WinTouch.POINTER_FLAGS.INCONTACT;
                    contact.pointerInfo.pointerFlags &= ~WinTouch.POINTER_FLAGS.DOWN;
                    contact.pointerInfo.pointerFlags |= WinTouch.POINTER_FLAGS.INRANGE | WinTouch.POINTER_FLAGS.UPDATE;
                }
                contactsToInject.Add(contact);
            }

            // 4. Inject the batch.
            if (contactsToInject.Count > 0)
            {
                WinTouch.InjectTouchInput(contactsToInject.Count, contactsToInject.ToArray());
            }

            // 5. If we sent an UP event, clean the persistent contacts that were removed.
            if (touchesToRemove.Count > 0)
            {
                foreach (var idToRemove in touchesToRemove)
                {
                    _persistentContacts.Remove(idToRemove);
                }
            }

            // 6. Always clear the temporary frame data for the next physics step.
            _activeFrameContacts.Clear();
            touchesToRemove.Clear();
        }
    }

    public class CaptureCore : MelonMod
    {
        public static MelonLogger.Instance Logger { get; private set; }
        private HarmonyLib.Harmony harmony;
        
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;


        private GameObject testTouchscreen;
        private static List<Process> runningCaptureProcesses = new List<Process>();


        public override void OnInitializeMelon()
        {
            Logger = LoggerInstance;
            harmony = new HarmonyLib.Harmony("com.yourname.windowcapture");

            var internalPowerOriginal = typeof(GameSystemState).GetMethod("InternalPower", BindingFlags.NonPublic | BindingFlags.Instance);
            var internalPowerPrefix = typeof(CaptureCore).GetMethod(nameof(OnInternalPowerPrefix));
            harmony.Patch(internalPowerOriginal, new HarmonyMethod(internalPowerPrefix));

            var retroarchOpenOriginal = typeof(Retroarch).GetMethod("Open", BindingFlags.Public | BindingFlags.Instance);
            var retroarchOpenPrefix = typeof(CaptureCore).GetMethod(nameof(OnRetroarchOpenPrefix));
            harmony.Patch(retroarchOpenOriginal, new HarmonyMethod(retroarchOpenPrefix));
        }
        [System.Obsolete]
        public override void OnApplicationStart()
        {
            Physics.autoSyncTransforms = true;
        }

        public override void OnApplicationQuit()
        {
            Logger.Msg("[CaptureCore] Application quitting. Terminating all capture processes.");
            foreach (var process in runningCaptureProcesses)
            {
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        Logger.Msg($"[CaptureCore] Terminating process {process.ProcessName} (ID: {process.Id}).");
                        process.Kill();
                    }
                }
                catch (Exception e) { Logger.Warning($"[CaptureCore] Failed to kill process on quit: {e.Message}"); }
            }
            runningCaptureProcesses.Clear();
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F2))
            {
                if (testTouchscreen == null)
                {
                    CreateTestTouchscreen();
                }
                else
                {
                    Object.Destroy(testTouchscreen);
                }
            }
        }
        
        public static bool OnRetroarchOpenPrefix(Retroarch __instance)
        {
            var gameSystem = __instance.GetComponent<GameSystem>();
            if (gameSystem != null && gameSystem.GetComponent<WindowCaptureInstance>() != null)
            {
                Logger.Msg("[CaptureCore] Retroarch.Open() intercepted. Preventing RetroArch launch.");
                __instance.isRunning = true;
                return false;
            }
            return true;
        }

        public static bool OnInternalPowerPrefix(GameSystemState __instance, bool on)
        {
            Game game = __instance.gameSystem.Game;
            if (game == null || string.IsNullOrEmpty(game.path)) return true;

            string emuVrRoot = Directory.GetParent(Application.dataPath).FullName;
            string absoluteGamePath = Path.Combine(emuVrRoot, game.path);

            string gameDirectory = Path.GetDirectoryName(absoluteGamePath);
            string gameNameWithoutExt = Path.GetFileNameWithoutExtension(absoluteGamePath);
            string winFilePath = Path.Combine(gameDirectory, gameNameWithoutExt + ".win");
            string batFilePath = Path.Combine(gameDirectory, gameNameWithoutExt + ".bat");

            if (!File.Exists(winFilePath) || !File.Exists(batFilePath)) return true;

            if (on)
            {
                if (__instance.gameSystem.gameObject.GetComponent<WindowCaptureInstance>() == null)
                {
                    Logger.Msg($"[CaptureCore] Intercepting Power-On for custom capture game '{game.name}'.");
                    string captureTarget = File.ReadAllText(winFilePath).Trim().Replace("\r\n", "\n");
                    if (string.IsNullOrEmpty(captureTarget))
                    {
                        Logger.Error($"[CaptureCore] .win file for '{game.name}' is empty. Aborting.");
                        return true;
                    }

                    var instance = __instance.gameSystem.gameObject.AddComponent<WindowCaptureInstance>();
                    instance.batPath = batFilePath;
                    instance.windowIdentifier = captureTarget;
                    string[] winFileLines = captureTarget.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    instance.gridConfiguration = winFileLines.Length > 2 ? winFileLines[2].Trim() : null;
                    __instance.gameSystem.NetworkretroarchIsRunning = true;
                }
            }
            else
            {
                var instance = __instance.gameSystem.GetComponent<WindowCaptureInstance>();
                if (instance != null)
                {
                    Logger.Msg($"[CaptureCore] Intercepting Power-Off for '{__instance.gameSystem.Game.name}'. Initiating cleanup.");
                    instance.SendMessage("StartCleanup", options: SendMessageOptions.DontRequireReceiver);
                }
            }
            return true;
        }

        public static void RegisterCaptureProcess(Process process)
        {
            runningCaptureProcesses.Add(process);
        }

        private void CreateTestTouchscreen()
        {
            testTouchscreen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            testTouchscreen.name = "TestWintouchScreen";

            float baseHeight = 0.9f;
            float aspectRatio = (float)CaptureCore.GetSystemMetrics(CaptureCore.SM_CXSCREEN) / CaptureCore.GetSystemMetrics(CaptureCore.SM_CYSCREEN);
            float newWidth = baseHeight * aspectRatio;

            testTouchscreen.transform.position = new Vector3(0, 1.2f, 1.5f);
            testTouchscreen.transform.rotation = Quaternion.Euler(0, 180, 0);
            testTouchscreen.transform.localScale = new Vector3(newWidth, baseHeight, 1.0f);

            Object.Destroy(testTouchscreen.GetComponent<Collider>());
            var physicalCollider = testTouchscreen.AddComponent<BoxCollider>();
            physicalCollider.size = new Vector3(1, 1, 0.05f);

            // Add a larger trigger collider for hover detection
            var hoverCollider = testTouchscreen.AddComponent<BoxCollider>();
            hoverCollider.isTrigger = true;
            // Make it slightly larger and thicker to detect the hand before it touches
            hoverCollider.size = new Vector3(1.1f, 1.1f, 0.4f); 

            var instance = testTouchscreen.AddComponent<WindowCaptureInstance>();
            instance.InitializeForTestScreen();
            Logger.Msg("[CaptureCore] Test touchscreen created. Press F2 again to destroy.");
        }
    }
    
    public class HandTouchInteraction : MonoBehaviour
    {
        private WindowCaptureInstance captureInstance;
        private BoxCollider physicalCollider;
        private BoxCollider hoverCollider;
        private uint touchId;

        private enum HandState { OutOfRange, Hovering, Drawing }
        private HandState currentState = HandState.OutOfRange;

        public void Initialize(WindowCaptureInstance instance, BoxCollider physical, BoxCollider hover, uint id)
        {
            captureInstance = instance;
            physicalCollider = physical;
            hoverCollider = hover;
            touchId = id;

            var handRb = gameObject.GetComponent<Rigidbody>();
            if (handRb == null)
            {
                handRb = gameObject.AddComponent<Rigidbody>();
                handRb.isKinematic = false;
                handRb.useGravity = false;
            }
            var handCollider = gameObject.GetComponent<SphereCollider>();
            if (handCollider == null)
            {
                handCollider = gameObject.AddComponent<SphereCollider>();
                handCollider.radius = 0.02f;
            }
        }

        void OnCollisionEnter(Collision collision)
        {
            if (collision.collider == physicalCollider)
            {
                currentState = HandState.Drawing;
                ContactPoint contact = collision.contacts[0]; // Use the first contact point
                captureInstance.UpdateTouchState(touchId, contact.point, true, transform);
            }
        }

        void OnCollisionStay(Collision collision)
        {
            if (collision.collider == physicalCollider)
            {
                currentState = HandState.Drawing;
                ContactPoint contact = collision.contacts[0]; // Use the first contact point
                captureInstance.UpdateTouchState(touchId, contact.point, false, transform);
                
                var handRb = GetComponent<Rigidbody>();
                if (handRb != null) handRb.velocity = Vector3.zero;
            }
        }

        void OnCollisionExit(Collision collision)
        {
            if (collision.collider == physicalCollider)
            {
                // We are no longer drawing. The OnTriggerStay will determine if we are now hovering or out of range.
                currentState = HandState.Hovering; 
                captureInstance.HandleDrawEnd(touchId);
            }
        }

        // --- Trigger Volume for Hovering ---
        void OnTriggerEnter(Collider other)
        {
            if (other.gameObject == gameObject && currentState == HandState.OutOfRange)
            if (other == hoverCollider && currentState == HandState.OutOfRange)
            {
                currentState = HandState.Hovering;
            }
        }

        void OnTriggerStay(Collider other)
        {
            if (other.gameObject == gameObject)
            if (other == hoverCollider)
            {
                // If we are not physically touching the screen, we are hovering.
                if (currentState == HandState.Hovering)
                {
                    Vector3 closestPoint = other.ClosestPoint(transform.position);
                    captureInstance.HandleHover(touchId, closestPoint, transform);
                }
            }
        }

        void OnTriggerExit(Collider other)
        {
            if (other.gameObject == gameObject)
            if (other == hoverCollider)
            {
                // If we were hovering, we are now out of range. Send an UP to cancel the hover.
                if (currentState == HandState.Hovering) captureInstance.HandleDrawEnd(touchId);
                currentState = HandState.OutOfRange;
            }
        }
    }

    public static class WinTouch
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_TOUCH_INFO
        {
            public POINTER_INFO pointerInfo;
            public TOUCH_FLAGS touchFlags;
            public TOUCH_MASK touchMask;
            public RECT contact;
            public RECT contactRaw;
            public uint orientation;
            public uint pressure;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_INFO
        {
            public POINTER_INPUT_TYPE pointerType;
            public uint pointerId;
            public uint frameId;
            public POINTER_FLAGS pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public POINT ptPixelLocation;
            public POINT ptHimetricLocation;
            public POINT ptPixelLocationRaw;
            public POINT ptHimetricLocationRaw;
            public uint dwTime;
            public uint historyCount;
            public int inputData;
            public uint dwKeyStates;
            public ulong PerformanceCount;
            public POINTER_BUTTON_CHANGE_TYPE ButtonChangeType;
        }

        public enum POINTER_INPUT_TYPE { PT_POINTER = 1, PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4 }

        [Flags]
        public enum POINTER_FLAGS : uint
        {
            NONE = 0x00000000, NEW = 0x00000001, INRANGE = 0x00000002, INCONTACT = 0x00000004,
            FIRSTBUTTON = 0x00000010, SECONDBUTTON = 0x00000020, THIRDBUTTON = 0x00000040, FOURTHBUTTON = 0x00000080, 
            FIFTHBUTTON = 0x00000100, PRIMARY = 0x00002000,
            CONFIDENCE = 0x00004000, CANCELED = 0x00008000, DOWN = 0x00010000,
            UPDATE = 0x00020000, UP = 0x00040000, WHEEL = 0x00080000, HWHEEL = 0x00100000,
            CAPTURECHANGED = 0x00200000
        }

        public enum POINTER_BUTTON_CHANGE_TYPE
        {
            NONE, FIRSTBUTTON_DOWN, FIRSTBUTTON_UP, SECONDBUTTON_DOWN, SECONDBUTTON_UP,
            THIRDBUTTON_DOWN, THIRDButton_UP, FOURTHBUTTON_DOWN, FOURTHBUTTON_UP,
            FIFTHBUTTON_DOWN, FIFTHBUTTON_UP
        }

        [Flags] public enum TOUCH_FLAGS { NONE = 0x00000000 }
        [Flags] public enum TOUCH_MASK { NONE = 0x00000000, CONTACTAREA = 0x00000001, ORIENTATION = 0x00000002, PRESSURE = 0x00000004 }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool InitializeTouchInjection(uint maxCount, TOUCH_FEEDBACK touchFeedback);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool InjectTouchInput(int count, [In] POINTER_TOUCH_INFO[] contacts);

        public enum TOUCH_FEEDBACK { DEFAULT = 0x1, INDIRECT = 0x2, NONE = 0x3 }
    }
}

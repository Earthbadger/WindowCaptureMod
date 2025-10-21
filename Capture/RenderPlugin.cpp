#include <d3d11.h>
#include <cstdint>
#include <string>
#include <string>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"

// Define the API for exporting functions from the DLL.
#if defined(_MSC_VER)
    #define UNITY_INTERFACE_EXPORT __declspec(dllexport)
    #define UNITY_INTERFACE_API __stdcall
#else
    #define UNITY_INTERFACE_EXPORT
#    define UNITY_INTERFACE_API
#endif

// --- Globals to hold pointers and data between C# and C++ ---
static IUnityInterfaces* g_UnityInterfaces = nullptr;
static IUnityGraphics* g_Graphics = nullptr;
static ID3D11Device* g_Device = nullptr;

static void* g_UnityTextureHandle = nullptr;      // The native texture pointer from Unity
static void* g_CurrentSharedHandle = nullptr;     // The shared handle from GraphicsCapture.exe
static ID3D11Texture2D* g_OpenedSharedTexture = nullptr; // Cached pointer to the opened shared texture
// --- Logging ---
typedef void (*DebugLogFunc)(const char*);
static DebugLogFunc g_DebugLog = nullptr;

void Log(const std::string& message) {
    if (g_DebugLog) {
        g_DebugLog(message.c_str());
    }
}

extern "C"
{
    UNITY_INTERFACE_EXPORT void SetLogFunction(DebugLogFunc logFunc)
    {
        g_DebugLog = logFunc;
    }

    // This function is called from C# to pass the shared handle to our plugin.
    UNITY_INTERFACE_EXPORT void SetSharedHandle(void* handle)
    {
        // This function now ONLY updates the handle.
        // The resource opening is tied to SetTextureFromUnity to ensure device compatibility.
        g_CurrentSharedHandle = handle;
    }

    // This function is called from C# to pass a pointer to the Unity-created texture.
    UNITY_INTERFACE_EXPORT void SetTextureFromUnity(void* texturePtr)
    {
        Log("[RenderPlugin] New Unity texture pointer received. Re-evaluating shared texture.");
        // If we have an old texture open, release it, as we are about to create a new one.
        if (g_OpenedSharedTexture) {
            g_OpenedSharedTexture->Release();
            g_OpenedSharedTexture = nullptr;
        }
        g_UnityTextureHandle = texturePtr;
    }

    // This is the function that will be called on the render thread.
    static void UNITY_INTERFACE_API OnRenderEvent(int eventID)
    {
        if (g_Device == nullptr || g_CurrentSharedHandle == nullptr || g_UnityTextureHandle == nullptr) return;

        ID3D11DeviceContext* context = nullptr;
        g_Device->GetImmediateContext(&context);
        if (!context) return;

        // If the shared texture isn't open yet AND we have a valid handle, open it.
        // This now happens only after SetTextureFromUnity has been called for a new texture,
        // ensuring the device context is correct.
        if (g_OpenedSharedTexture == nullptr && g_CurrentSharedHandle != nullptr)
        {
            Log("[RenderPlugin] Opening shared resource handle.");
            HRESULT hr = g_Device->OpenSharedResource(g_CurrentSharedHandle, __uuidof(ID3D11Texture2D), (void**)(&g_OpenedSharedTexture));
            if (FAILED(hr))
            {
                Log("[RenderPlugin] ERROR: Failed to open shared resource. HRESULT: " + std::to_string(hr));
                // Set handle to null to prevent repeated failed attempts until a new handle is provided.
                g_CurrentSharedHandle = nullptr;
                context->Release();
                return;
            }
            Log("[RenderPlugin] Successfully opened shared resource.");
        }

        if (g_OpenedSharedTexture != nullptr)
        {
            ID3D11Resource* unityTexture = (ID3D11Resource*)g_UnityTextureHandle;
            context->CopyResource(unityTexture, g_OpenedSharedTexture);
        }

        context->Release();
    }

    // Unity calls this function when the plugin is loaded.
    UNITY_INTERFACE_EXPORT void UnityPluginLoad(IUnityInterfaces* unityInterfaces)
    {
        g_UnityInterfaces = unityInterfaces;
        g_Graphics = g_UnityInterfaces->Get<IUnityGraphics>();
        IUnityGraphicsD3D11* d3d11 = g_UnityInterfaces->Get<IUnityGraphicsD3D11>();
        if (d3d11 != nullptr)
        {
            g_Device = d3d11->GetDevice();
            Log("[RenderPlugin] D3D11 Device acquired.");
        }
        else
        {
            Log("[RenderPlugin] ERROR: Failed to get D3D11 interface.");
        }
    }

    UNITY_INTERFACE_EXPORT void UnityPluginUnload()
    {
        Log("[RenderPlugin] Unloading.");
        if (g_OpenedSharedTexture) {
            g_OpenedSharedTexture->Release();
            g_OpenedSharedTexture = nullptr;
        }
    }

    // This function returns a pointer to our render event function.
    UNITY_INTERFACE_EXPORT void* GetRenderEventFunc()
    {
        return OnRenderEvent;
    }
}

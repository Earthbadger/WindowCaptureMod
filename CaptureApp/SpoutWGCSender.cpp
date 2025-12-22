// SpoutWGCSender.cpp
// A standalone application to capture any window using Windows Graphics Capture
// and send it via Spout, avoiding DLL injection.

#include <iostream>
#include <string>
#include <mutex>
#include <atomic>
#include <vector>

// Windows & DirectX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dwmapi.h>
#include <psapi.h>

// C++/WinRT
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

// Interop
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

// DispatcherQueue C API
#include <DispatcherQueue.h>

// Spout
#include "SpoutDX.h"

// Link necessary libraries
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "windowsapp.lib") 

// Use namespace aliases to avoid "ambiguous symbol" errors with windows.h
namespace WGC = winrt::Windows::Graphics::Capture;
namespace WGD = winrt::Windows::Graphics::DirectX::Direct3D11;
namespace WGDX = winrt::Windows::Graphics::DirectX;
namespace WF = winrt::Windows::Foundation;

// Ensure IDirect3DDxgiInterfaceAccess is defined
// This interface allows access to the underlying DXGI surface from a WinRT Direct3DSurface
extern "C" {
    struct __declspec(uuid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")) IDirect3DDxgiInterfaceAccess : ::IUnknown
    {
        virtual HRESULT __stdcall GetInterface(GUID const& id, void** object) = 0;
    };
}

// Global state
ID3D11Device* g_d3dDevice = nullptr;
ID3D11DeviceContext* g_d3dContext = nullptr;
spoutDX g_spout;
std::mutex g_frameMutex;
ID3D11Texture2D* g_latestFrame = nullptr;
std::atomic<bool> g_newFrameAvailable = false;
std::atomic<bool> g_captureClosed = false;

// Helper to create a WinRT Direct3DDevice from a native ID3D11Device
WGD::IDirect3DDevice CreateDirect3DDevice(ID3D11Device* d3dDevice)
{
    IDXGIDevice* dxgiDevice = nullptr;
    HRESULT hr = d3dDevice->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDevice);
    if (FAILED(hr)) return nullptr;

    WGD::IDirect3DDevice winrtDevice = nullptr;
    // This function is exported by d3d11.dll but requires the interop header
    hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, reinterpret_cast<IInspectable**>(winrt::put_abi(winrtDevice)));
    
    dxgiDevice->Release();
    return winrtDevice;
}

// Helper to find a window by Title OR Executable Name
HWND FindTargetWindow(const std::string& target) {
    // 1. Try finding by exact window title first
    HWND hwnd = FindWindowA(nullptr, target.c_str());
    if (hwnd) return hwnd;

    // 2. Try finding by executable name (e.g. "game.exe")
    struct SearchData {
        std::string targetExe;
        HWND result = nullptr;
    } searchData;
    searchData.targetExe = target;

    // Enumerate all top-level windows
    EnumWindows([](HWND hwnd, LPARAM lParam) -> BOOL {
        auto* data = reinterpret_cast<SearchData*>(lParam);
        
        // Skip invisible windows to avoid capturing background processes
        if (!IsWindowVisible(hwnd)) return TRUE;

        DWORD pid;
        GetWindowThreadProcessId(hwnd, &pid);
        
        HANDLE hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (hProcess) {
            char buffer[MAX_PATH];
            if (GetProcessImageFileNameA(hProcess, buffer, MAX_PATH)) {
                std::string fullPath = buffer;
                // Extract just the filename from the path
                std::string exeName = fullPath.substr(fullPath.find_last_of("\\/") + 1);
                if (_stricmp(exeName.c_str(), data->targetExe.c_str()) == 0) {
                    data->result = hwnd;
                    CloseHandle(hProcess);
                    return FALSE; // Stop enumeration
                }
            }
            CloseHandle(hProcess);
        }
        return TRUE; // Continue enumeration
    }, reinterpret_cast<LPARAM>(&searchData));

    return searchData.result;
}

int main(int argc, char* argv[])
{
    // Initialize COM for C++/WinRT
    // WGC events require a message pump and often an STA thread with a DispatcherQueue
    winrt::init_apartment(winrt::apartment_type::single_threaded);

    // Create a DispatcherQueue for the current thread to handle WGC events using the C API
    // This avoids "CreateOnCurrentThread is not a member" errors.
    DispatcherQueueOptions options
    {
        sizeof(DispatcherQueueOptions),
        DQTYPE_THREAD_CURRENT,
        DQTAT_COM_STA
    };
    winrt::Windows::System::DispatcherQueueController controller{ nullptr };
    winrt::check_hresult(CreateDispatcherQueueController(options, reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(controller))));

    bool closedByTarget = false;

    try
    {
        // 1. Initialize DirectX 11
        // We need BGRA support for Windows Graphics Capture
        D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_0 };
        UINT createDeviceFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

#ifdef _DEBUG
        // createDeviceFlags |= D3D11_CREATE_DEVICE_DEBUG;
#endif

        HRESULT hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            createDeviceFlags, featureLevels, 1,
            D3D11_SDK_VERSION, &g_d3dDevice, nullptr, &g_d3dContext);

        if (FAILED(hr)) {
            std::cerr << "Failed to create D3D11 device." << std::endl;
            return 1;
        }

        // 2. Initialize Spout
        // SpoutDX handles the sharing of the DX11 texture
        if (!g_spout.OpenDirectX11(g_d3dDevice)) {
            std::cerr << "Failed to initialize SpoutDX." << std::endl;
            return 1;
        }

        char senderName[256] = "GameCaptureWGC";
        g_spout.SetSenderName(senderName);

        // 3. Select Window
        std::string windowTitle;
        bool disableCursor = false;

        // Check CLI arguments
        if (argc > 1) {
            windowTitle = argv[1];
            for (int i = 2; i < argc; ++i) {
                if (std::string(argv[i]) == "--no-cursor") disableCursor = true;
            }
        } else {
            // Fallback to manual input
            std::cout << "Enter window title OR executable name (e.g. game.exe): ";
            std::getline(std::cin, windowTitle);
        }

        // Find the window
        HWND hwnd = FindTargetWindow(windowTitle);
        if (!hwnd) {
            std::cerr << "Window not found!" << std::endl;
            return 1;
        }

        // Ensure window is not minimized, otherwise capture might pause
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        // 4. Create GraphicsCaptureItem from HWND
        auto activationFactory = winrt::get_activation_factory<WGC::GraphicsCaptureItem>();
        auto interopFactory = activationFactory.as<IGraphicsCaptureItemInterop>();

        WGC::GraphicsCaptureItem item = { nullptr };
        // 0x3E68D... is the GUID for IGraphicsCaptureItemInterop
        interopFactory->CreateForWindow(hwnd, winrt::guid_of<WGC::GraphicsCaptureItem>(), winrt::put_abi(item));

        if (!item) {
            std::cerr << "Failed to create capture item. Is the window valid?" << std::endl;
            return 1;
        }

        // Handle window closing event to auto-close this program
        auto closedToken = item.Closed([&](auto&&, auto&&) {
            g_captureClosed = true;
        });

        // 5. Setup Capture Session
        WGD::IDirect3DDevice winrtDevice = CreateDirect3DDevice(g_d3dDevice);
        auto itemSize = item.Size();
        auto lastSize = itemSize;

        // Create a frame pool. 2 buffers is usually enough for double buffering.
        auto framePool = WGC::Direct3D11CaptureFramePool::Create(
            winrtDevice,
            WGDX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2, itemSize);

        auto session = framePool.CreateCaptureSession(item);

        // Disable cursor capture if requested
        if (disableCursor) session.IsCursorCaptureEnabled(false);

        // 6. Handle Frame Arrived
        // This lambda runs on a worker thread managed by the OS
        framePool.FrameArrived([&](WGC::Direct3D11CaptureFramePool const& sender, winrt::Windows::Foundation::IInspectable const&)
            {
                auto frame = sender.TryGetNextFrame();
                if (!frame) return;

                auto contentSize = frame.ContentSize();
                if ((contentSize.Width != lastSize.Width || contentSize.Height != lastSize.Height) && 
                    contentSize.Width > 0 && contentSize.Height > 0)
                {
                    lastSize = contentSize;
                    sender.Recreate(
                        winrtDevice,
                        WGDX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                        2,
                        lastSize);
                }

                auto surface = frame.Surface();
                auto access = surface.as<IDirect3DDxgiInterfaceAccess>();

                ID3D11Texture2D* texture = nullptr;
                access->GetInterface(IID_PPV_ARGS(&texture));

                if (texture) {
                    std::lock_guard<std::mutex> lock(g_frameMutex);
                    if (g_latestFrame) g_latestFrame->Release();
                    g_latestFrame = texture; // Take ownership
                    g_newFrameAvailable = true;
                }
            });

        // 7. Start Capture
        session.StartCapture();
        std::cout << "Capturing '" << windowTitle << "' to Spout sender '" << senderName << "'" << std::endl;
        std::cout << "Press END to stop..." << std::endl;

        // 8. Main Loop
        // We process frames here to keep Spout operations on the main thread
        bool hasLoggedFrame = false;
        MSG msg = {};

        while (true) {
            // Pump messages (Crucial for WGC events to fire)
            while (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessage(&msg);
            }

            // Check for exit
            // Use VK_END instead of VK_RETURN to avoid immediate exit if the Enter key is still held down
            if (GetAsyncKeyState(VK_END) & 0x8000) break;

            if (g_captureClosed || !IsWindow(hwnd)) {
                closedByTarget = true;
                break;
            }

            if (g_newFrameAvailable) {
                ID3D11Texture2D* textureToSend = nullptr;

                {
                    std::lock_guard<std::mutex> lock(g_frameMutex);
                    if (g_latestFrame) {
                        textureToSend = g_latestFrame;
                        g_latestFrame->AddRef(); // Add ref for local use
                        g_newFrameAvailable = false;
                    }
                }

                if (textureToSend) {
                    // Send via Spout
                    // SpoutDX handles the creation of the shared texture and copy internally
                    if (!g_spout.SendTexture(textureToSend)) {
                        std::cerr << "Spout::SendTexture failed!" << std::endl;
                    }

                    // Flush the immediate context to ensure the copy is submitted to the GPU
                    g_d3dContext->Flush();

                    textureToSend->Release();
                }
            }

            // Yield slightly to avoid burning 100% CPU in the loop
            // In a real game loop, you would sync this to VSync or a timer
            Sleep(1);
        }

        // Cleanup
        session.Close();
        framePool.Close();

        {
            std::lock_guard<std::mutex> lock(g_frameMutex);
            if (g_latestFrame) g_latestFrame->Release();
        }

        g_spout.ReleaseSender();
        g_d3dContext->Release();
        g_d3dDevice->Release();
    }
    catch (winrt::hresult_error const& ex)
    {
        std::wcerr << L"Capture failed with error: " << ex.message().c_str() << std::endl;
    }

    if (!closedByTarget) {
        std::cout << "\nApplication finished. Press ENTER to exit." << std::endl;
        std::cin.get();
    }

    return 0;
}

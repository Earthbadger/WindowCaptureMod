// main.cpp

#define _WIN32_WINNT 0x0A00

#include <iostream>
#include <string>
#include <vector>
#include <Windows.h>
#include <dwmapi.h>
#include <fstream>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <dxgi1_6.h>
#include <winrt/Windows.System.h>

#include <inspectable.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h> 
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.UI.Composition.h>
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dwmapi.lib")

// For finding window by process name
#include <TlHelp32.h>

std::ofstream log_file;

void Log(const std::string& message) {
    time_t now = time(0);
    tm ltm;
    localtime_s(&ltm, &now);
    char time_buf[9];
    strftime(time_buf, sizeof(time_buf), "%H:%M:%S", &ltm);
    if (log_file.is_open()) log_file << "[" << time_buf << "] " << message << std::endl;
    std::cout << "[" << time_buf << "] " << message << std::endl;
}
 
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

MIDL_INTERFACE("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")
IDirect3DDxgiInterfaceAccess : public ::IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE GetInterface(REFIID iid, _COM_Outptr_ void** p) = 0;
};

struct SharedMemory
{
    HANDLE file_handle = nullptr;
    void* buffer = nullptr;
    std::string name;
    uint32_t width = 0;
    uint32_t height = 0;
    uint64_t texture_handle = 0; 
    HWND hwnd = NULL;
};

struct EnumData {
    DWORD process_id;
    HWND window_handle;
};

auto GetNativeTexture(IDirect3DSurface const& surface)
{
    auto access = surface.as<IDirect3DDxgiInterfaceAccess>();
    com_ptr<ID3D11Texture2D> native_texture;
    check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), native_texture.put_void()));
    return native_texture;
}

HWND FindWindowByProcessName(const std::wstring& processName)
{
    PROCESSENTRY32W entry;
    entry.dwSize = sizeof(PROCESSENTRY32W);
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, NULL);
    DWORD target_pid = 0;
    if (Process32FirstW(snapshot, &entry) == TRUE) {
        while (Process32NextW(snapshot, &entry) == TRUE) {
            if (_wcsicmp(entry.szExeFile, processName.c_str()) == 0) {
                target_pid = entry.th32ProcessID;
                break;
            }
        }
    }
    CloseHandle(snapshot);
    if (target_pid == 0) return NULL;

    EnumData data = { target_pid, NULL };
    EnumWindows([](HWND hwnd, LPARAM lParam) -> BOOL {
        auto& data = *reinterpret_cast<EnumData*>(lParam);
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid == data.process_id) {
            if (IsWindowVisible(hwnd) && GetParent(hwnd) == NULL) {
                data.window_handle = hwnd;
                return FALSE;
            }
        }
        return TRUE;
    }, (LPARAM)&data);
    return data.window_handle;
}

struct CaptureState {
    Direct3D11CaptureFramePool frame_pool{ nullptr };
    GraphicsCaptureSession session{ nullptr };
    GraphicsCaptureItem item{ nullptr };
    IDirect3DDevice device{ nullptr };
    com_ptr<ID3D11Device> d3d_device;
    com_ptr<ID3D11DeviceContext> d3d_context;
    SharedMemory sm;
    com_ptr<ID3D11Texture2D> shared_texture;
    HANDLE shared_handle = nullptr;
    bool capture_running = true;
    winrt::event_token frame_arrived_token;
    bool needs_reinitialization = false;
    uint32_t polled_width = 0;
    uint32_t polled_height = 0;
    HWND captured_hwnd = NULL;
    LONG_PTR original_style = 0;
};

void OnFrameArrived(Direct3D11CaptureFramePool const& sender, winrt::Windows::Foundation::IInspectable const&, CaptureState* state)
{
    auto frame = sender.TryGetNextFrame();
    if (state->needs_reinitialization || !frame || !state->capture_running) { return; }
    auto frame_texture = GetNativeTexture(frame.Surface());
    state->d3d_context->CopyResource(state->shared_texture.get(), frame_texture.get());
}

bool InitializeWinGfxCapture(CaptureState& state, HWND hwnd)
{
    Log("Initializing capture using Windows.Graphics.Capture API...");
    state.shared_texture = nullptr;
    check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, state.d3d_device.put(), nullptr, state.d3d_context.put()));
    Log("DirectX device created.");
    com_ptr<IDXGIDevice> dxgi_device = state.d3d_device.as<IDXGIDevice>();
    com_ptr<::IInspectable> inspectable;
    check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi_device.get(), inspectable.put()));
    state.device = inspectable.as<IDirect3DDevice>();
    Log("WinRT device created.");
    
    auto interop_factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    if (hwnd != NULL) {
        check_hresult(interop_factory->CreateForWindow(hwnd, guid_of<GraphicsCaptureItem>(), put_abi(state.item)));
        Log("Capture item created for window.");
    } else {
        // Desktop capture mode
        HMONITOR hmonitor = MonitorFromWindow(GetDesktopWindow(), MONITOR_DEFAULTTOPRIMARY);
        check_hresult(interop_factory->CreateForMonitor(hmonitor, guid_of<GraphicsCaptureItem>(), put_abi(state.item)));
        Log("Capture item created for primary monitor.");
    }

    auto frame_size = state.item.Size();
    state.sm.width = static_cast<uint32_t>(frame_size.Width);
    state.sm.height = static_cast<uint32_t>(frame_size.Height);
    Log("Capture item reports size: " + std::to_string(state.sm.width) + "x" + std::to_string(state.sm.height));
    if (state.sm.width == 0 || state.sm.height == 0) {
        Log("ERROR: Capture item has invalid dimensions (0). Aborting.");
        state.needs_reinitialization = true;
        return false;
    }
    size_t buffer_size = 24; // W+H+Handle+HWND
    if (state.sm.buffer) UnmapViewOfFile(state.sm.buffer);
    if (state.sm.file_handle) CloseHandle(state.sm.file_handle);
    state.sm.file_handle = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, (DWORD)buffer_size, state.sm.name.c_str());
    if (state.sm.file_handle == NULL) {
        Log("[FATAL] Could not create file mapping object: " + std::to_string(GetLastError()));
        return false;
    }
    state.sm.buffer = MapViewOfFile(state.sm.file_handle, FILE_MAP_ALL_ACCESS, 0, 0, buffer_size);
    if (state.sm.buffer == nullptr) {
        Log("[FATAL] Could not map view of file: " + std::to_string(GetLastError()));
        return false;
    }
    Log("Shared memory '" + state.sm.name + "' created. Size: " + std::to_string(buffer_size) + " bytes.");
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = state.sm.width;
    desc.Height = state.sm.height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
    check_hresult(state.d3d_device->CreateTexture2D(&desc, nullptr, state.shared_texture.put()));
    Log("Created shared D3D11 texture.");
    com_ptr<IDXGIResource> dxgi_resource = state.shared_texture.as<IDXGIResource>();
    check_hresult(dxgi_resource->GetSharedHandle(&state.shared_handle));
    Log("Obtained shared handle for texture.");
    uint32_t* header = static_cast<uint32_t*>(state.sm.buffer);
    header[0] = state.sm.width;
    header[1] = state.sm.height;
    uint64_t* handle_ptr = reinterpret_cast<uint64_t*>(static_cast<byte*>(state.sm.buffer) + 8);
    *handle_ptr = reinterpret_cast<uint64_t>(state.shared_handle);
    uint64_t* hwnd_ptr = reinterpret_cast<uint64_t*>(static_cast<byte*>(state.sm.buffer) + 16);
    *hwnd_ptr = reinterpret_cast<uint64_t>(hwnd);
    Log("Wrote initial header: " + std::to_string(state.sm.width) + "x" + std::to_string(state.sm.height) + " with texture handle and HWND.");
    state.frame_pool = Direct3D11CaptureFramePool::CreateFreeThreaded(state.device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, state.item.Size());
    Log("Frame pool created.");
    state.session = state.frame_pool.CreateCaptureSession(state.item);
    return true;
}

int main(int argc, char* argv[])
{
    // Make the process DPI aware to handle high-DPI displays correctly.
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    // --- HYBRID LOADER ---
    // This executable can now act as a standard graphics capturer OR a DLL injector.

    char exe_path[MAX_PATH];
    GetModuleFileNameA(NULL, exe_path, MAX_PATH);
    std::string exe_dir = std::string(exe_path);
    size_t last_slash = exe_dir.find_last_of("\\/");
    std::string log_path = (last_slash != std::string::npos) ? exe_dir.substr(0, last_slash) : ".";
    log_path += "\\GraphicsCapture.log";
    log_file.open(log_path, std::ios::out | std::ios::trunc);

    CaptureState state;

    
    std::string target_name;
    std::string mem_name;
    bool hook_mode = false;
    bool desktop_mode = false;

    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--target" && i + 1 < argc) {
            target_name = argv[++i];
        }
        else if (arg == "--memname" && i + 1 < argc) {
            mem_name = argv[++i];
        }
        else if (arg == "--hook") {
            hook_mode = true;
        }
        else if (arg == "--desktop") {
            desktop_mode = true;
        }
    }

    if ((target_name.empty() && !desktop_mode) || mem_name.empty()) {
        Log("[FATAL] Usage: --target \"process.exe\" --memname \"SharedMemoryName\" [--hook]");
        return 1;
    }

    if (hook_mode)
    {
        // --- D3D9 HOOKING LOADER LOGIC ---
        Log("Hook mode activated for target: " + target_name);

        size_t buffer_size = 24; // W+H+Handle+HWND
        HANDLE sm_handle = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, (DWORD)buffer_size, mem_name.c_str());
        if (sm_handle == NULL) {
            Log("[FATAL] Could not create file mapping object: " + std::to_string(GetLastError()));
            return 1;
        }
        Log("Shared memory file mapping created: " + mem_name);

        STARTUPINFOA si = { sizeof(si) };
        PROCESS_INFORMATION pi = {};
        std::string cmd = "\"" + target_name + "\"";

        std::string env_block = "CAPTURE_MEMNAME=" + mem_name + '\0' + "CAPTURE_LOGPATH=" + log_path + '\0';

        Log("Launching target process in suspended state: " + target_name);
        if (!CreateProcessA(NULL, (LPSTR)cmd.c_str(), NULL, NULL, FALSE, CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, (LPVOID)env_block.c_str(), NULL, &si, &pi)) {
            Log("[FATAL] CreateProcess failed: " + std::to_string(GetLastError()));
            CloseHandle(sm_handle);
            return 1;
        }

        char dll_path[MAX_PATH];
        GetFullPathNameA("CaptureHook.dll", MAX_PATH, dll_path, NULL);
        Log("Injecting DLL: " + std::string(dll_path));

        LPVOID p_dll_path = VirtualAllocEx(pi.hProcess, 0, strlen(dll_path) + 1, MEM_COMMIT, PAGE_READWRITE);
        if (!p_dll_path) {
            Log("[FATAL] VirtualAllocEx failed: " + std::to_string(GetLastError()));
            TerminateProcess(pi.hProcess, 1); return 1;
        }

        if (!WriteProcessMemory(pi.hProcess, p_dll_path, (LPVOID)dll_path, strlen(dll_path) + 1, 0)) {
            Log("[FATAL] WriteProcessMemory failed: " + std::to_string(GetLastError()));
            TerminateProcess(pi.hProcess, 1); return 1;
        }

        HANDLE h_thread = CreateRemoteThread(pi.hProcess, 0, 0, (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleA("Kernel32"), "LoadLibraryA"), p_dll_path, 0, 0);
        if (!h_thread) {
            Log("[FATAL] CreateRemoteThread failed: " + std::to_string(GetLastError()));
            TerminateProcess(pi.hProcess, 1); return 1;
        }

        WaitForSingleObject(h_thread, INFINITE);
        CloseHandle(h_thread);
        VirtualFreeEx(pi.hProcess, p_dll_path, 0, MEM_RELEASE);

        Log("DLL injected. Resuming target process.");
        ResumeThread(pi.hThread);

        Log("Loader is now waiting for the target process to exit...");
        WaitForSingleObject(pi.hProcess, INFINITE);

        Log("Target process has exited.");
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        CloseHandle(sm_handle);
    }
    else
    {
        if (desktop_mode) {
            Log("Desktop capture mode activated.");
        } else {
            Log("Standard capture mode activated for target: " + target_name);
        }

        winrt::init_apartment();        
        state.sm.name = mem_name;

        while (state.capture_running) {
            HWND hwnd = NULL;
            if (!desktop_mode) {
                std::wstring w_target_name(target_name.begin(), target_name.end());
                while (state.capture_running && (hwnd == NULL || !IsWindow(hwnd) || IsIconic(hwnd))) {
                    hwnd = FindWindowByProcessName(w_target_name);
                    if (hwnd == NULL) {
                        Log("Searching for target window: " + target_name);
                        Log("Target window not found. Retrying in 2 seconds...");
                        Sleep(2000);
                    }
                }
            }

            // In desktop mode, hwnd is NULL, so we skip the window check.
            if (!state.capture_running || (!desktop_mode && !IsWindow(hwnd))) {
                Log("Target window seems to have closed. Exiting.");
                break;
            }

            if (!desktop_mode) {
                Log("Window found! Handle: " + std::to_string((intptr_t)hwnd));
                state.captured_hwnd = hwnd;

                Log("Applying borderless window style to ensure compatibility...");
                state.original_style = GetWindowLongPtr(hwnd, GWL_STYLE);
                LONG_PTR new_style = state.original_style & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
                new_style |= WS_POPUP;
                SetWindowLongPtr(hwnd, GWL_STYLE, new_style);
                SetWindowPos(hwnd, NULL, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                Log("Window style applied.");
            }

            try {
                state.session = nullptr;
                state.frame_pool = nullptr;
                state.item = nullptr;
                state.device = nullptr;
                state.needs_reinitialization = false;

                HANDLE closed_event = NULL;
                winrt::event_token closed_token{};

                if (!InitializeWinGfxCapture(state, hwnd)) {
                    Log("WinGfx capture initialization failed. Retrying...");
                    Sleep(1000);
                    continue;
                }

                closed_event = CreateEvent(NULL, TRUE, FALSE, NULL);
                closed_token = state.item.Closed([&](auto const&, auto const&) { SetEvent(closed_event); });
                state.frame_arrived_token = state.frame_pool.FrameArrived([&](auto const& sender, auto const& args) { OnFrameArrived(sender, args, &state); });
                Log("Starting capture session...");
                state.session.StartCapture();

                if (!desktop_mode) {
                    // Set the initial polled size *after* initialization to prevent an immediate re-init loop.
                    RECT initial_rect;
                    GetClientRect(hwnd, &initial_rect);
                    state.polled_width = initial_rect.right;
                    state.polled_height = initial_rect.bottom;
                }

                bool keep_running = true;
                while (keep_running) {
                    // Only check for a valid window if we are not in desktop mode.
                    if (!desktop_mode) {
                        if (!IsWindow(hwnd)) {
                            Log("Target window disappeared during capture. Re-initializing search.");
                            keep_running = false;
                            break;
                        }
                    }

                    if (!desktop_mode) {
                        RECT client_rect;
                        GetClientRect(hwnd, &client_rect);
                        if (client_rect.right != state.polled_width || client_rect.bottom != state.polled_height) {
                            Log("Window client rect changed. Signaling for re-initialization.");
                            keep_running = false;
                            state.needs_reinitialization = true;
                        }
                    }
                    
                    DWORD wait_result = MsgWaitForMultipleObjects(1, &closed_event, FALSE, 1000, QS_ALLINPUT);
                    if (wait_result == WAIT_OBJECT_0) {
                        keep_running = false;
                    } else if (wait_result == WAIT_OBJECT_0 + 1) {
                        MSG msg;
                        while (PeekMessage(&msg, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessage(&msg); }
                    }
                }
                if (closed_event) CloseHandle(closed_event);
                if (state.item) state.item.Closed(closed_token);
                if (state.frame_pool) state.frame_pool.FrameArrived(state.frame_arrived_token);
                if (state.session) state.session.Close();
                if (state.frame_pool) state.frame_pool.Close();
            } catch (winrt::hresult_error const& e) {
                Log("ERROR during capture: " + winrt::to_string(e.message()));
                Sleep(1000);
            }
        }
    }

    if (state.captured_hwnd && IsWindow(state.captured_hwnd) && state.original_style != 0) {
        Log("Restoring original window style...");
        SetWindowLongPtr(state.captured_hwnd, GWL_STYLE, state.original_style);
        SetWindowPos(state.captured_hwnd, NULL, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        Log("Window style restored.");
    }

    if (log_file.is_open()) log_file.close();

    Log("Loader/Capture shutdown complete.");
    return 0;
}

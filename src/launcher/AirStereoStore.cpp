// Only included in MSIX packages. Unpackaged EXE/MSI builds never load this DLL.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <roapi.h>
#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Foundation.h>
#include <chrono>

namespace
{
    // Managed callers use a worker thread; no synchronous WinRT waits on the UI thread.
    struct Apartment
    {
        bool owned;
        Apartment()
        {
            HRESULT result = RoInitialize(RO_INIT_MULTITHREADED);
            owned = SUCCEEDED(result);
            if (FAILED(result) && result != RPC_E_CHANGED_MODE) winrt::throw_hresult(result);
        }
        ~Apartment() { if (owned) RoUninitialize(); }
    };

    template<typename Operation>
    auto BoundedResult(Operation const& operation)
    {
        if (operation.wait_for(std::chrono::seconds(8)) == winrt::Windows::Foundation::AsyncStatus::Started)
        {
            operation.Cancel();
            winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
        }
        return operation.get();
    }
}

extern "C" __declspec(dllexport) HRESULT __stdcall AirStereoStartupState(INT* state) noexcept
{
    try
    {
        if (!state) return E_POINTER;
        Apartment apartment;
        auto task = BoundedResult(winrt::Windows::ApplicationModel::StartupTask::GetAsync(L"AirStereoStartup"));
        *state = static_cast<INT>(task.State());
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __stdcall AirStereoSetStartup(INT enabled, INT* state) noexcept
{
    try
    {
        if (!state) return E_POINTER;
        Apartment apartment;
        auto task = BoundedResult(winrt::Windows::ApplicationModel::StartupTask::GetAsync(L"AirStereoStartup"));
        if (enabled)
            *state = static_cast<INT>(BoundedResult(task.RequestEnableAsync()));
        else
        {
            task.Disable();
            *state = static_cast<INT>(task.State());
        }
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __stdcall AirStereoDataDirectory(WCHAR* path, UINT capacity) noexcept
{
    try
    {
        if (!path || !capacity) return E_POINTER;
        Apartment apartment;
        auto folder = winrt::Windows::Storage::ApplicationData::Current().LocalFolder().Path();
        if (folder.size() >= capacity) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        wcscpy_s(path, capacity, folder.c_str());
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

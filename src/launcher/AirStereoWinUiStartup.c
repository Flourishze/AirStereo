#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

/* Use the WinUI apphost; the application owns the silent-start preference. */
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    WCHAR executable[32768];
    WCHAR command[32768];
    STARTUPINFOW startup = { sizeof(startup) };
    PROCESS_INFORMATION process = { 0 };
    DWORD length = GetModuleFileNameW(NULL, executable, ARRAYSIZE(executable));
    if (!length || length >= ARRAYSIZE(executable)) return 1;
    WCHAR *slash = wcsrchr(executable, L'\\');
    if (!slash) return 1;
    slash[1] = L'\0';
    if (wcscat_s(executable, ARRAYSIZE(executable), L"AirStereo.exe") != 0) return 1;
    if (swprintf_s(command, ARRAYSIZE(command), L"\"%s\" gui --tray", executable) < 0) return 1;
    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &process))
        return (int)GetLastError();
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}

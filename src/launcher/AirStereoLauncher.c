/*
 * AirStereo.exe - the double-clickable entry point.
 *
 * The machine this was built on has the .NET runtimes but no SDK, so there is no apphost
 * template to link against. This tiny stub starts "dotnet AirStereo.dll <args>" instead.
 *
 * It passes the command line through untouched, so the same executable can still be used
 * from a command prompt; only when there is no console to inherit (a double-click from
 * Explorer) does it hide the child console and fall back to the window.
 */

#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <stdio.h>
#include <wchar.h>

#define TEXT_LIMIT 4096

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand)
{
    (void)instance;
    (void)previous;
    (void)showCommand;

    wchar_t module[TEXT_LIMIT];
    DWORD length = GetModuleFileNameW(NULL, module, TEXT_LIMIT);
    if (length == 0 || length >= TEXT_LIMIT)
        return 1;

    wchar_t directory[TEXT_LIMIT];
    wcsncpy_s(directory, TEXT_LIMIT, module, TEXT_LIMIT - 1);
    wchar_t *slash = wcsrchr(directory, L'\\');
    if (slash == NULL)
        return 1;
    *slash = L'\0';

#ifdef AIRSTEREO_STARTUP
    // Windows startup-task launches stay in the tray; direct launches show the panel.
    const wchar_t *arguments = (commandLine != NULL && commandLine[0] != L'\0') ? commandLine : L"gui --tray";
#else
    const wchar_t *arguments = (commandLine != NULL && commandLine[0] != L'\0') ? commandLine : L"gui";
#endif

    /* Installed packages carry their matching runtime. Development builds still use PATH. */
    wchar_t host[TEXT_LIMIT];
    if (_snwprintf_s(host, TEXT_LIMIT, _TRUNCATE, L"%ls\\dotnet.exe", directory) < 0)
        return 1;
    DWORD hostAttributes = GetFileAttributesW(host);
    if (hostAttributes == INVALID_FILE_ATTRIBUTES || (hostAttributes & FILE_ATTRIBUTE_DIRECTORY))
        wcscpy_s(host, TEXT_LIMIT, L"dotnet.exe");

    wchar_t command[TEXT_LIMIT * 2];
    if (_snwprintf_s(command, TEXT_LIMIT * 2, _TRUNCATE,
            L"\"%ls\" \"%ls\\AirStereo.dll\" %ls", host, directory, arguments) < 0)
        return 1;

    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);

    /* A console we can write to is inherited; without one the child must stay quiet,
       otherwise the window would be pushed aside by a stray console window. */
    DWORD flags = (GetConsoleWindow() != NULL) ? 0 : CREATE_NO_WINDOW;

    /* The real sender is this stub's child, so an outside kill aimed at AirStereo.exe would
       otherwise leave an invisible dotnet.exe running and holding the speakers. A job object
       that dies with its last handle ties the two lifetimes together. */
    HANDLE job = CreateJobObjectW(NULL, NULL);
    if (job != NULL)
    {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits;
        ZeroMemory(&limits, sizeof(limits));
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                &limits, sizeof(limits)))
        {
            CloseHandle(job);
            job = NULL;
        }
    }

    if (!CreateProcessW(NULL, command, NULL, NULL, FALSE, flags, NULL, directory, &startup, &process))
    {
        if (job != NULL) CloseHandle(job);
        MessageBoxW(NULL,
            L"\u627e\u4e0d\u5230 dotnet.exe\u3002\u8bf7\u5b89\u88c5\u5f53\u524d\u7248\u672c\u7684 .NET \u684c\u9762\u8fd0\u884c\u65f6\uff08Windows Desktop Runtime\uff09\u3002",
            L"AirStereo", MB_OK | MB_ICONERROR);
        return 2;
    }

    if (job != NULL)
    {
        AssignProcessToJobObject(job, process.hProcess);
    }

    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    /* The job handle is held for the whole run on purpose: KILL_ON_JOB_CLOSE only fires once
       the last handle goes away, which is the signal that this stub is gone too. */
    if (job != NULL) CloseHandle(job);
    return (int)code;
}

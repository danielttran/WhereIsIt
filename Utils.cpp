#include "framework.h"
#include "Utils.h"
#include <sddl.h>
#include <iostream>
#include <chrono>
#include <ctime>

bool Logger::m_enabled = true; 

void Logger::Log(const std::wstring& message) {
    if (!m_enabled) return;
    
    auto now = std::chrono::system_clock::now();
    auto in_time_t = std::chrono::system_clock::to_time_t(now);
    struct tm buf;
    localtime_s(&buf, &in_time_t);
    
    wchar_t timeStr[32];
    wcsftime(timeStr, 32, L"%Y-%m-%d %H:%M:%S", &buf);
    
    std::wstring out = std::wstring(timeStr) + L" - " + message + L"\n";
    OutputDebugStringW(out.c_str());
}

static SECURITY_ATTRIBUTES* BuildSa(const wchar_t* sddl)
{
    struct SaHolder {
        SECURITY_ATTRIBUTES attrs{ sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE };
        PSECURITY_DESCRIPTOR sd = nullptr;
        explicit SaHolder(const wchar_t* inSddl) {
            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    inSddl, SDDL_REVISION_1, &sd, nullptr)) {
                attrs.lpSecurityDescriptor = sd;
            }
        }
        ~SaHolder() {
            if (sd) LocalFree(sd);
        }
    };

    // Keep lifetime for process duration; objects may be created throughout runtime.
    static SaHolder pipeSa(
        // LocalSystem + Builtin Administrators full control.
        // Authenticated Users can connect/read/write to named-pipe transactions.
        L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;AU)");
    static SaHolder sharedReadSa(
        // LocalSystem + Builtin Administrators full control.
        // Authenticated Users read/synchronize only for shared data and mutex.
        L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;AU)");
    static SaHolder serviceOnlySa(
        L"D:(A;;GA;;;SY)(A;;GA;;;BA)");

    if (sddl == nullptr) return &sharedReadSa.attrs;
    if (wcscmp(sddl, L"pipe") == 0) return &pipeSa.attrs;
    if (wcscmp(sddl, L"service") == 0) return &serviceOnlySa.attrs;
    return &sharedReadSa.attrs;
}

SECURITY_ATTRIBUTES* GetPipeServerSA() {
    return BuildSa(L"pipe");
}

SECURITY_ATTRIBUTES* GetSharedMemoryReadOnlySA() {
    return BuildSa(L"shared");
}

SECURITY_ATTRIBUTES* GetServiceOnlySA() {
    return BuildSa(L"service");
}

bool RegisterContextMenu() {
    wchar_t exePath[MAX_PATH];
    if (!GetModuleFileNameW(NULL, exePath, MAX_PATH)) return false;

    std::wstring command = std::wstring(L"\"") + exePath + L"\" \"%1\"";

    HKEY hKey;
    if (RegCreateKeyExW(HKEY_CLASSES_ROOT, L"Directory\\shell\\WhereIsIt", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hKey, NULL) == ERROR_SUCCESS) {
        RegSetValueExW(hKey, L"", 0, REG_SZ, (const BYTE*)L"Search with WhereIsIt", sizeof(L"Search with WhereIsIt"));
        RegSetValueExW(hKey, L"Icon", 0, REG_SZ, (const BYTE*)exePath, (DWORD)((wcslen(exePath) + 1) * sizeof(wchar_t)));
        
        HKEY hCommandKey;
        if (RegCreateKeyExW(hKey, L"command", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hCommandKey, NULL) == ERROR_SUCCESS) {
            RegSetValueExW(hCommandKey, L"", 0, REG_SZ, (const BYTE*)command.c_str(), (DWORD)((command.size() + 1) * sizeof(wchar_t)));
            RegCloseKey(hCommandKey);
        }
        RegCloseKey(hKey);
    }
    
    // Also for drives
    if (RegCreateKeyExW(HKEY_CLASSES_ROOT, L"Drive\\shell\\WhereIsIt", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hKey, NULL) == ERROR_SUCCESS) {
        RegSetValueExW(hKey, L"", 0, REG_SZ, (const BYTE*)L"Search with WhereIsIt", sizeof(L"Search with WhereIsIt"));
        RegSetValueExW(hKey, L"Icon", 0, REG_SZ, (const BYTE*)exePath, (DWORD)((wcslen(exePath) + 1) * sizeof(wchar_t)));
        
        HKEY hCommandKey;
        if (RegCreateKeyExW(hKey, L"command", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hCommandKey, NULL) == ERROR_SUCCESS) {
            RegSetValueExW(hCommandKey, L"", 0, REG_SZ, (const BYTE*)command.c_str(), (DWORD)((command.size() + 1) * sizeof(wchar_t)));
            RegCloseKey(hCommandKey);
        }
        RegCloseKey(hKey);
    }
    return true;
}

bool UnregisterContextMenu() {
    RegDeleteTreeW(HKEY_CLASSES_ROOT, L"Directory\\shell\\WhereIsIt");
    RegDeleteTreeW(HKEY_CLASSES_ROOT, L"Drive\\shell\\WhereIsIt");
    return true;
}

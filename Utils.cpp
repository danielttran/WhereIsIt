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

SECURITY_ATTRIBUTES* GetPermissiveSA() {
    // C++11 guarantees this static is initialized exactly once, thread-safely.
    static SECURITY_ATTRIBUTES sa = []() -> SECURITY_ATTRIBUTES {
        SECURITY_ATTRIBUTES attrs = { sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE };
        PSECURITY_DESCRIPTOR pSD = nullptr;
        // D:(A;;GA;;;WD) = Allow Generic All to World (Everyone)
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;WD)", SDDL_REVISION_1, &pSD, nullptr))
            attrs.lpSecurityDescriptor = pSD;
        return attrs;
    }();
    return &sa;
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

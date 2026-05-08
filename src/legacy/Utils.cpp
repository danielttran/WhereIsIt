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
        bool valid = false;
        explicit SaHolder(const wchar_t* inSddl) {
            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    inSddl, SDDL_REVISION_1, &sd, nullptr)) {
                attrs.lpSecurityDescriptor = sd;
                valid = true;
            } else {
                Logger::Log(std::wstring(L"Failed to convert SDDL: ") + inSddl);
            }
        }
        ~SaHolder() {
            if (sd) LocalFree(sd);
        }
    };

    static SaHolder pipeSa(L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGWGX;;;WD)");
    static SaHolder sharedReadSa(L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;WD)");
    static SaHolder serviceOnlySa(L"D:(A;;GA;;;SY)(A;;GA;;;BA)");

    if (sddl == nullptr) return sharedReadSa.valid ? &sharedReadSa.attrs : nullptr;
    if (wcscmp(sddl, L"pipe") == 0) return pipeSa.valid ? &pipeSa.attrs : nullptr;
    if (wcscmp(sddl, L"service") == 0) return serviceOnlySa.valid ? &serviceOnlySa.attrs : nullptr;
    return sharedReadSa.valid ? &sharedReadSa.attrs : nullptr;
}

SECURITY_ATTRIBUTES* GetPipeServerSA() {
    return BuildSa(L"pipe");
}

SECURITY_ATTRIBUTES* GetSharedMemoryReadOnlySA() {
    return BuildSa(nullptr);
}

SECURITY_ATTRIBUTES* GetServiceOnlySA() {
    return BuildSa(L"service");
}

bool RegisterContextMenu() {
    std::vector<wchar_t> exePathBuf(MAX_PATH);
    DWORD len = 0;
    while (true) {
        len = GetModuleFileNameW(NULL, exePathBuf.data(), (DWORD)exePathBuf.size());
        if (len == 0) return false;
        if (len < exePathBuf.size()) break;
        exePathBuf.resize(exePathBuf.size() * 2);
    }
    std::wstring exePath(exePathBuf.data(), len);

    std::wstring command = std::wstring(L"\"") + exePath + L"\" \"%1\"";

    UniqueHkey hKey;
    if (RegCreateKeyExW(HKEY_CLASSES_ROOT, L"Directory\\shell\\WhereIsIt", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, hKey.put(), NULL) == ERROR_SUCCESS) {
        RegSetValueExW(hKey, L"", 0, REG_SZ, (const BYTE*)L"Search with WhereIsIt", sizeof(L"Search with WhereIsIt"));
        RegSetValueExW(hKey, L"Icon", 0, REG_SZ, (const BYTE*)exePath.c_str(), (DWORD)((exePath.length() + 1) * sizeof(wchar_t)));
        
        UniqueHkey hCommandKey;
        if (RegCreateKeyExW(hKey, L"command", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, hCommandKey.put(), NULL) == ERROR_SUCCESS) {
            RegSetValueExW(hCommandKey, L"", 0, REG_SZ, (const BYTE*)command.c_str(), (DWORD)((command.size() + 1) * sizeof(wchar_t)));
        }
    }
    
    // Also for drives
    if (RegCreateKeyExW(HKEY_CLASSES_ROOT, L"Drive\\shell\\WhereIsIt", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, hKey.put(), NULL) == ERROR_SUCCESS) {
        RegSetValueExW(hKey, L"", 0, REG_SZ, (const BYTE*)L"Search with WhereIsIt", sizeof(L"Search with WhereIsIt"));
        RegSetValueExW(hKey, L"Icon", 0, REG_SZ, (const BYTE*)exePath.c_str(), (DWORD)((exePath.length() + 1) * sizeof(wchar_t)));
        
        UniqueHkey hCommandKey;
        if (RegCreateKeyExW(hKey, L"command", 0, NULL, REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, hCommandKey.put(), NULL) == ERROR_SUCCESS) {
            RegSetValueExW(hCommandKey, L"", 0, REG_SZ, (const BYTE*)command.c_str(), (DWORD)((command.size() + 1) * sizeof(wchar_t)));
        }
    }
    return true;
}

bool UnregisterContextMenu() {
    RegDeleteTreeW(HKEY_CLASSES_ROOT, L"Directory\\shell\\WhereIsIt");
    RegDeleteTreeW(HKEY_CLASSES_ROOT, L"Drive\\shell\\WhereIsIt");
    return true;
}

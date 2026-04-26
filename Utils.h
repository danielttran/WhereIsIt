#pragma once
#include <windows.h>
#include <string>

SECURITY_ATTRIBUTES* GetPermissiveSA();
bool SetPrivilege(HANDLE hToken, LPCTSTR lpszPrivilege, bool bEnablePrivilege);

bool RegisterContextMenu();
bool UnregisterContextMenu();

class Logger {
public:
    static void Log(const std::wstring& message);
    static void SetEnabled(bool enabled) { m_enabled = enabled; }
    static bool IsEnabled() { return m_enabled; }
private:
    static bool m_enabled;
};

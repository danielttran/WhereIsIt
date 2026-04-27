#pragma once
#include <windows.h>
#include <string>

// Pipe server SA: SYSTEM and Administrators get full control;
// Authenticated Users get read+write (GRGW) so the UI process can send queries.
SECURITY_ATTRIBUTES* GetPipeServerSA();

// Shared-memory SA: SYSTEM and Administrators get full control;
// Authenticated Users get read-only (GR) so the UI can map views read-only.
SECURITY_ATTRIBUTES* GetSharedMemoryReadOnlySA();

// Service-only SA: only SYSTEM and Administrators; used for objects that the
// UI process must never access directly (e.g. service-internal mutexes).
SECURITY_ATTRIBUTES* GetServiceOnlySA();

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

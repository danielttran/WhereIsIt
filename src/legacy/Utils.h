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

class UniqueHandle {
    HANDLE m_h;
public:
    UniqueHandle() : m_h(nullptr) {}
    explicit UniqueHandle(HANDLE h) : m_h(h == INVALID_HANDLE_VALUE ? nullptr : h) {}
    ~UniqueHandle() { if (m_h) CloseHandle(m_h); }
    UniqueHandle(UniqueHandle&& other) noexcept : m_h(other.m_h) { other.m_h = nullptr; }
    UniqueHandle& operator=(UniqueHandle&& other) noexcept {
        if (this != &other) {
            if (m_h) CloseHandle(m_h);
            m_h = other.m_h;
            other.m_h = nullptr;
        }
        return *this;
    }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    
    operator HANDLE() const { return m_h; }
    HANDLE get() const { return m_h; }
    bool is_valid() const { return m_h != nullptr; }
    void reset(HANDLE h = nullptr) {
        if (m_h) CloseHandle(m_h);
        m_h = (h == INVALID_HANDLE_VALUE ? nullptr : h);
    }
    HANDLE* put() { reset(); return &m_h; }
};

class UniqueHkey {
    HKEY m_h;
public:
    UniqueHkey() : m_h(nullptr) {}
    explicit UniqueHkey(HKEY h) : m_h(h) {}
    ~UniqueHkey() { if (m_h) RegCloseKey(m_h); }
    UniqueHkey(UniqueHkey&& other) noexcept : m_h(other.m_h) { other.m_h = nullptr; }
    UniqueHkey& operator=(UniqueHkey&& other) noexcept {
        if (this != &other) {
            if (m_h) RegCloseKey(m_h);
            m_h = other.m_h;
            other.m_h = nullptr;
        }
        return *this;
    }
    UniqueHkey(const UniqueHkey&) = delete;
    UniqueHkey& operator=(const UniqueHkey&) = delete;
    
    operator HKEY() const { return m_h; }
    HKEY* put() { reset(); return &m_h; }
    void reset(HKEY h = nullptr) {
        if (m_h) RegCloseKey(m_h);
        m_h = h;
    }
};

class UniqueMapView {
    void* m_ptr;
public:
    UniqueMapView() : m_ptr(nullptr) {}
    explicit UniqueMapView(void* ptr) : m_ptr(ptr) {}
    ~UniqueMapView() { if (m_ptr) UnmapViewOfFile(m_ptr); }
    UniqueMapView(UniqueMapView&& other) noexcept : m_ptr(other.m_ptr) { other.m_ptr = nullptr; }
    UniqueMapView& operator=(UniqueMapView&& other) noexcept {
        if (this != &other) {
            if (m_ptr) UnmapViewOfFile(m_ptr);
            m_ptr = other.m_ptr;
            other.m_ptr = nullptr;
        }
        return *this;
    }
    UniqueMapView(const UniqueMapView&) = delete;
    UniqueMapView& operator=(const UniqueMapView&) = delete;
    
    void* get() const { return m_ptr; }
    bool is_valid() const { return m_ptr != nullptr; }
    void reset(void* ptr = nullptr) {
        if (m_ptr) UnmapViewOfFile(m_ptr);
        m_ptr = ptr;
    }
};

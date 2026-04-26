#include <array>
#pragma once
#include "Engine.h"
#include <atomic>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <string>
#include <vector>
#include <memory>

#define WHEREISIT_PIPE_NAME       L"\\\\.\\pipe\\WhereIsItIPC"
#define WHEREISIT_SERVICE_NAME    L"WhereIsIt"
#define WHEREISIT_SERVICE_DISPLAY L"WhereIsIt File Indexer"

// Returns true if the named pipe server is reachable.
// Uses a CreateFile probe so the check is reliable even when all server
// instances are momentarily busy (WaitNamedPipeW with 1 ms would miss that).
bool IsNamedPipeServerAvailable();

// Installs the executable as a Windows service (SERVICE_AUTO_START, LocalSystem).
// Returns 0 on success, Win32 error code on failure.
int ServiceInstall();

// Removes the WhereIsIt Windows service.
int ServiceUninstall();

// Runs the named pipe server loop, serving search requests via engine.
// Blocks until hStopEvent is signaled.
void RunNamedPipeServerLoop(IndexingEngine* engine, HANDLE hStopEvent);

// Entry point for -svc mode: calls StartServiceCtrlDispatcher.
// Returns 0 on success, Win32 error code on failure.
int RunAsService();

// ---------------------------------------------------------------------------
// NamedPipeEngine
// IIndexEngine proxy that routes Search() over the WhereIsItIPC named pipe.
// Sort() re-sorts the received result set locally using m_local's record
// accessors. All other record-accessor calls also delegate to m_local, which
// loads index.dat from disk on Start().
// ---------------------------------------------------------------------------
class NamedPipeEngine : public IIndexEngine {
public:
    NamedPipeEngine();
    ~NamedPipeEngine() override;

    void Start() override;
    void Stop()  override;

    void Search(const std::string& query) override;
    void Sort(QuerySortKey key, bool descending) override;
    std::shared_ptr<std::vector<uint32_t>> GetSearchResults() override;

    // SetNotifyWindow is called once from the UI thread before any search.
    // m_hwndNotify is atomic so SearchWorker can read it without a lock.
    void SetNotifyWindow(HWND hwnd) override
    {
        m_hwndNotify.store(hwnd, std::memory_order_release);
    }

    FileRecord                           GetRecord(uint32_t idx) const override;
    uint64_t                             GetRecordFileSize(uint32_t idx) const override;
    uint64_t                             GetRecordLastModifiedFileTime(uint32_t idx) const override;
    std::wstring                         GetRecordName(uint32_t idx) const override;
    std::pair<FileRecord, std::wstring>  GetRecordAndName(uint32_t idx) const override;

    std::wstring GetCurrentStatus() const override;
    std::wstring GetFullPath(uint32_t idx)   const override;
    std::wstring GetParentPath(uint32_t idx) const override;

    void             SetIndexScopeConfig(const IndexScopeConfig& cfg) override;
    IndexScopeConfig GetIndexScopeConfig() const override;

private:
    void SearchWorker();

    // Sort the result vector in-place using m_local record accessors.
    void ApplySort(std::vector<uint32_t>& v, QuerySortKey key, bool descending);

    // Atomic so SetNotifyWindow (UI thread) and SearchWorker (worker thread)
    // do not race. HWND is a pointer so atomic<HWND> is lock-free on all
    // supported Windows platforms.
    std::atomic<HWND>        m_hwndNotify{ nullptr };

    // Local engine used solely for record-accessor calls (GetRecord, GetFullPath…).
    // (Phase 2): Now eliminated. The UI directly maps the background service's memory.
    
    HANDLE m_hRecordsMapping = NULL;
    FileRecord* m_recordsShared = nullptr;
    std::atomic<uint32_t>* m_recordsCount = nullptr;
    HANDLE m_hDataMutex = NULL;
    HANDLE m_hDrivesMapping = NULL;
    wchar_t (*m_driveLettersShared)[4] = nullptr;

    // String pool dynamic mapping
    #include <array>
    mutable std::array<std::atomic<char*>, 100> m_uiChunks;
    mutable std::mutex m_chunkMutex;
    const char* GetString(uint32_t offset) const;

    std::thread              m_searchThread;
    std::atomic<bool>        m_running{ false };

    // All fields below are protected by m_searchMutex.
    std::mutex               m_searchMutex;
    std::condition_variable  m_searchCv;
    std::string              m_pendingQuery;
    bool                     m_searchPending = false;  // new query waiting
    bool                     m_sortPending   = false;  // sort-only re-sort needed
    QuerySortKey             m_sortKey       = QuerySortKey::Name;
    bool                     m_sortDescending = false;

    mutable std::mutex                     m_resultsMutex;
    std::shared_ptr<std::vector<uint32_t>> m_results;

    // Persistent 4 MB receive buffer — avoids a heap alloc on every search.
    // Accessed only from SearchWorker; no locking needed.
    std::vector<char>        m_rxBuf;
};

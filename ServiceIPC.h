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

// Returns true if the named pipe server is reachable (non-blocking probe).
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
// All record-accessor calls delegate to an embedded local IndexingEngine that
// loads index.dat from disk.
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

    void SetNotifyWindow(HWND hwnd) override { m_hwndNotify = hwnd; }

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

    HWND                     m_hwndNotify  = nullptr;
    IndexingEngine           m_local;          // record-accessor fallback

    std::thread              m_searchThread;
    std::atomic<bool>        m_running{ false };
    std::mutex               m_searchMutex;
    std::condition_variable  m_searchCv;
    std::string              m_pendingQuery;
    bool                     m_searchPending = false;

    mutable std::mutex                     m_resultsMutex;
    std::shared_ptr<std::vector<uint32_t>> m_results;
};

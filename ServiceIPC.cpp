#include "framework.h"
#include "ServiceIPC.h"
#include <chrono>
#include <algorithm>
#include <vector>
#include <string>

// Max response payload: 1 M result indices × 4 bytes + 4-byte count header.
// Both the server write buffer and the client receive buffer are sized to this.
static constexpr DWORD kMaxResultBytes  = 4u * 1024u * 1024u + sizeof(uint32_t);
static constexpr DWORD kMaxResultCount  = (kMaxResultBytes - sizeof(uint32_t)) / sizeof(uint32_t);
static constexpr DWORD kPipeCallTimeout = 5000;  // ms, passed to CallNamedPipeW
static constexpr int   kSearchTimeoutMs = 5000;  // ms, server polls for engine results

// ---------------------------------------------------------------------------
// Availability check
// ---------------------------------------------------------------------------

bool IsNamedPipeServerAvailable()
{
    // Attempt an immediate connection. If the server exists but every instance
    // is momentarily busy, CreateFile returns ERROR_PIPE_BUSY (not
    // ERROR_FILE_NOT_FOUND), so we still correctly report the server as up.
    // (WaitNamedPipeW with 1 ms is unreliable under load.)
    HANDLE h = CreateFileW(
        WHEREISIT_PIPE_NAME,
        GENERIC_READ | GENERIC_WRITE,
        0, nullptr, OPEN_EXISTING, 0, nullptr);

    if (h != INVALID_HANDLE_VALUE) {
        CloseHandle(h);
        return true;
    }
    return GetLastError() == ERROR_PIPE_BUSY;
}

// ---------------------------------------------------------------------------
// Service install / uninstall
// ---------------------------------------------------------------------------

int ServiceInstall()
{
    wchar_t exePath[MAX_PATH];
    if (!GetModuleFileNameW(nullptr, exePath, MAX_PATH))
        return static_cast<int>(GetLastError());

    std::wstring cmd = L"\"";
    cmd += exePath;
    cmd += L"\" -svc";

    SC_HANDLE hScm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CREATE_SERVICE);
    if (!hScm) return static_cast<int>(GetLastError());

    SC_HANDLE hSvc = CreateServiceW(
        hScm,
        WHEREISIT_SERVICE_NAME, WHEREISIT_SERVICE_DISPLAY,
        SERVICE_ALL_ACCESS,
        SERVICE_WIN32_OWN_PROCESS,
        SERVICE_AUTO_START,
        SERVICE_ERROR_NORMAL,
        cmd.c_str(),
        nullptr, nullptr, nullptr, nullptr, nullptr);

    int result = 0;
    if (!hSvc) result = static_cast<int>(GetLastError());
    else       CloseServiceHandle(hSvc);

    CloseServiceHandle(hScm);
    return result;
}

int ServiceUninstall()
{
    SC_HANDLE hScm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    if (!hScm) return static_cast<int>(GetLastError());

    SC_HANDLE hSvc = OpenServiceW(
        hScm, WHEREISIT_SERVICE_NAME,
        DELETE | SERVICE_STOP | SERVICE_QUERY_STATUS);
    if (!hSvc) {
        int err = static_cast<int>(GetLastError());
        CloseServiceHandle(hScm);
        return err;
    }

    SERVICE_STATUS ss{};
    ControlService(hSvc, SERVICE_CONTROL_STOP, &ss);

    int result = 0;
    if (!DeleteService(hSvc)) result = static_cast<int>(GetLastError());

    CloseServiceHandle(hSvc);
    CloseServiceHandle(hScm);
    return result;
}

// ---------------------------------------------------------------------------
// Named pipe server loop
// ---------------------------------------------------------------------------

// Poll GetSearchResults() until it returns a pointer different from prevResults
// or the timeout elapses. Returns a non-null (possibly empty) vector.
static std::shared_ptr<std::vector<uint32_t>> WaitForNewResults(
    IndexingEngine* engine,
    const std::shared_ptr<std::vector<uint32_t>>& prevResults)
{
    auto r = engine->WaitForNewResults(prevResults, kSearchTimeoutMs);
    return r ? r : std::make_shared<std::vector<uint32_t>>();
}

void RunNamedPipeServerLoop(IndexingEngine* engine, HANDLE hStopEvent)
{
    HANDLE hResultsMap = NULL;
    for (;;) {
        if (WaitForSingleObject(hStopEvent, 0) == WAIT_OBJECT_0) break;

        HANDLE hPipe = CreateNamedPipeW(
            WHEREISIT_PIPE_NAME,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            kMaxResultBytes, 4096, 0, nullptr);

        if (hPipe == INVALID_HANDLE_VALUE) break;

        // ---- Wait for a client, interruptible by hStopEvent ----
        bool clientConnected = false;
        {
            OVERLAPPED ov{};
            ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!ov.hEvent) { CloseHandle(hPipe); break; }

            ConnectNamedPipe(hPipe, &ov);
            DWORD connectErr = GetLastError();

            if (connectErr == ERROR_PIPE_CONNECTED) {
                // Client connected before ConnectNamedPipe was called.
                clientConnected = true;
            } else if (connectErr == ERROR_IO_PENDING) {
                HANDLE waitSet[2] = { hStopEvent, ov.hEvent };
                DWORD  w = WaitForMultipleObjects(2, waitSet, FALSE, INFINITE);

                if (w == WAIT_OBJECT_0) {
                    // Stop requested.
                    CancelIo(hPipe);
                    CloseHandle(ov.hEvent);
                    CloseHandle(hPipe);
                    return;
                }
                if (w == WAIT_OBJECT_0 + 1) {
                    DWORD dummy = 0;
                    clientConnected =
                        GetOverlappedResult(hPipe, &ov, &dummy, FALSE) != FALSE;
                }
                // If w == WAIT_FAILED: clientConnected stays false → pipe closed below.
            }
            CloseHandle(ov.hEvent);
        }

        if (!clientConnected) {
            CloseHandle(hPipe);
            continue;
        }

        // ---- Read request: [uint32_t queryLen][char query[queryLen]] ----
        char  readBuf[4096];
        DWORD bytesRead = 0;
        {
            OVERLAPPED rdOv{};
            rdOv.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!rdOv.hEvent) {
                DisconnectNamedPipe(hPipe);
                CloseHandle(hPipe);
                continue;
            }

            BOOL ok = ReadFile(hPipe, readBuf, sizeof(readBuf), &bytesRead, &rdOv);
            if (!ok && GetLastError() == ERROR_IO_PENDING) {
                ok = GetOverlappedResult(hPipe, &rdOv, &bytesRead, TRUE);
                if (!ok) CancelIo(hPipe);
            } else if (!ok) {
                CancelIo(hPipe);
            }

            CloseHandle(rdOv.hEvent);

            if (!ok || bytesRead < sizeof(uint32_t)) {
                DisconnectNamedPipe(hPipe);
                CloseHandle(hPipe);
                continue;
            }
        }

        uint32_t queryLen = 0;
        memcpy(&queryLen, readBuf, sizeof(queryLen));
        if (queryLen > bytesRead - sizeof(uint32_t)) {
            DisconnectNamedPipe(hPipe);
            CloseHandle(hPipe);
            continue;
        }

        std::string query(readBuf + sizeof(uint32_t), queryLen);

        // ---- Run search and wait for results ----
        auto prevResults = engine->GetSearchResults();
        engine->Search(query);
        auto results = WaitForNewResults(engine, prevResults);

        // ---- Write response: [uint32_t count] ----
        uint32_t count = static_cast<uint32_t>(
            (std::min)(results->size(), static_cast<size_t>(kMaxResultCount)));

        if (count > 0) {
            if (hResultsMap) CloseHandle(hResultsMap);
            hResultsMap = CreateFileMappingW(INVALID_HANDLE_VALUE, GetPermissiveSA(), PAGE_READWRITE, 0, count * sizeof(uint32_t), L"Global\\WhereIsIt_Results");
            if (hResultsMap) {
                void* pView = MapViewOfFile(hResultsMap, FILE_MAP_WRITE, 0, 0, count * sizeof(uint32_t));
                if (pView) {
                    memcpy(pView, results->data(), count * sizeof(uint32_t));
                    UnmapViewOfFile(pView);
                }
            }
        }

        DWORD responseSize = sizeof(uint32_t);
        {
            OVERLAPPED wrOv{};
            wrOv.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (wrOv.hEvent) {
                DWORD written = 0;
                BOOL  ok = WriteFile(hPipe, &count, responseSize, &written, &wrOv);
                if (!ok && GetLastError() == ERROR_IO_PENDING) {
                    ok = GetOverlappedResult(hPipe, &wrOv, &written, TRUE);
                    if (!ok) CancelIo(hPipe);
                } else if (!ok) {
                    CancelIo(hPipe);
                }
                CloseHandle(wrOv.hEvent);
            }
        }

        FlushFileBuffers(hPipe);
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }
    if (hResultsMap) CloseHandle(hResultsMap);
}

// ---------------------------------------------------------------------------
// Windows Service boilerplate
// ---------------------------------------------------------------------------

static SERVICE_STATUS_HANDLE g_hServiceStatus    = nullptr;
static HANDLE                g_hServiceStopEvent  = nullptr;
static IndexingEngine*       g_pServiceEngine     = nullptr;

static void ReportServiceStatus(DWORD state, DWORD exitCode, DWORD waitHint)
{
    static DWORD checkpoint = 1;
    SERVICE_STATUS ss{};
    ss.dwServiceType      = SERVICE_WIN32_OWN_PROCESS;
    ss.dwCurrentState     = state;
    ss.dwControlsAccepted = (state == SERVICE_RUNNING)
                            ? (SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN)
                            : 0;
    ss.dwWin32ExitCode    = exitCode;
    ss.dwWaitHint         = waitHint;
    ss.dwCheckPoint       =
        (state == SERVICE_RUNNING || state == SERVICE_STOPPED) ? 0 : checkpoint++;
    SetServiceStatus(g_hServiceStatus, &ss);
}

static DWORD WINAPI HandlerEx(
    DWORD  control,
    DWORD  eventType,
    LPVOID eventData,
    LPVOID context)
{
    UNREFERENCED_PARAMETER(eventType);
    UNREFERENCED_PARAMETER(eventData);
    UNREFERENCED_PARAMETER(context);

    if (control == SERVICE_CONTROL_STOP || control == SERVICE_CONTROL_SHUTDOWN) {
        ReportServiceStatus(SERVICE_STOP_PENDING, NO_ERROR, 5000);
        SetEvent(g_hServiceStopEvent);
    }
    return NO_ERROR;
}

static void WINAPI ServiceMain(DWORD argc, LPWSTR* argv)
{
    UNREFERENCED_PARAMETER(argc);
    UNREFERENCED_PARAMETER(argv);

    g_hServiceStatus = RegisterServiceCtrlHandlerExW(
        WHEREISIT_SERVICE_NAME, HandlerEx, nullptr);
    if (!g_hServiceStatus) return;

    ReportServiceStatus(SERVICE_START_PENDING, NO_ERROR, 3000);

    g_hServiceStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_hServiceStopEvent) {
        ReportServiceStatus(SERVICE_STOPPED, GetLastError(), 0);
        return;
    }

    g_pServiceEngine = new IndexingEngine();
    g_pServiceEngine->Start();

    ReportServiceStatus(SERVICE_RUNNING, NO_ERROR, 0);

    RunNamedPipeServerLoop(g_pServiceEngine, g_hServiceStopEvent);

    g_pServiceEngine->Stop();
    delete g_pServiceEngine;
    g_pServiceEngine = nullptr;

    CloseHandle(g_hServiceStopEvent);
    g_hServiceStopEvent = nullptr;

    ReportServiceStatus(SERVICE_STOPPED, NO_ERROR, 0);
}

int RunAsService()
{
    wchar_t svcName[] = WHEREISIT_SERVICE_NAME;
    SERVICE_TABLE_ENTRYW dispatchTable[] = {
        { svcName, ServiceMain },
        { nullptr, nullptr }
    };
    return StartServiceCtrlDispatcherW(dispatchTable)
           ? 0
           : static_cast<int>(GetLastError());
}

// ---------------------------------------------------------------------------
// NamedPipeEngine — IIndexEngine proxy over WhereIsItIPC
// ---------------------------------------------------------------------------

static uint64_t UnixEpochSecondsToFileTime(uint32_t epochSeconds) {
    return (uint64_t)epochSeconds * 10000000ULL + 116444736000000000ULL;
}

NamedPipeEngine::NamedPipeEngine()  = default;
NamedPipeEngine::~NamedPipeEngine() { Stop(); }

void NamedPipeEngine::Start()
{
    if (m_running.exchange(true)) return;

    // Phase 2: Attach to Shared Memory instead of running local scan
    m_hDataMutex = OpenMutexW(MUTEX_ALL_ACCESS, FALSE, L"Global\\WhereIsIt_DataMutex");
    m_hRecordsMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Global\\WhereIsIt_Records");
    m_hDrivesMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Global\\WhereIsIt_Drives");
    if (m_hDrivesMapping) {
        m_driveLettersShared = (wchar_t(*)[4])MapViewOfFile(m_hDrivesMapping, FILE_MAP_READ, 0, 0, 0);
    }
    if (m_hRecordsMapping) {
        uint8_t* base = (uint8_t*)MapViewOfFile(m_hRecordsMapping, FILE_MAP_READ, 0, 0, 0);
        if (base) {
            m_recordsCount = (std::atomic<uint32_t>*)base;
            m_recordsShared = (FileRecord*)(base + sizeof(std::atomic<uint32_t>));
        }
    }


    for (auto& c : m_uiChunks) {
        c.store(nullptr, std::memory_order_relaxed);
    }

    m_searchThread = std::thread(&NamedPipeEngine::SearchWorker, this);
}

void NamedPipeEngine::Stop()
{
    if (!m_running.exchange(false)) return;

    CancelSynchronousIo(m_searchThread.native_handle());

    if (m_searchThread.joinable())
        m_searchThread.join();

    if (m_recordsCount) UnmapViewOfFile(m_recordsCount);
    if (m_hRecordsMapping) CloseHandle(m_hRecordsMapping);
    if (m_driveLettersShared) UnmapViewOfFile(m_driveLettersShared);
    if (m_hDrivesMapping) CloseHandle(m_hDrivesMapping);
    if (m_hDataMutex) CloseHandle(m_hDataMutex);

    std::lock_guard<std::mutex> lock(m_chunkMutex);
    for (auto& c : m_uiChunks) {
        char* ptr = c.load(std::memory_order_relaxed);
        if (ptr) { UnmapViewOfFile(ptr); c.store(nullptr, std::memory_order_relaxed); }
    }

}

void NamedPipeEngine::Search(const std::string& query)
{
    {
        std::lock_guard<std::mutex> lk(m_searchMutex);
        m_pendingQuery  = query;
        m_searchPending = true;
    }
    m_searchCv.notify_one();
}

void NamedPipeEngine::Sort(QuerySortKey key, bool descending)
{
    {
        std::lock_guard<std::mutex> lk(m_searchMutex);
        m_sortKey        = key;
        m_sortDescending = descending;
        m_sortPending    = true;
        m_searchPending  = false;  // sort-only — do not re-issue search query
    }
    m_searchCv.notify_one();
}

// Sort v in-place by key/descending using m_local record accessors.
// Uses _wcsicmp for case-insensitive comparison, mirroring IndexingEngine.
// Falls back to Name comparison when Size or Date values are equal.
void NamedPipeEngine::ApplySort(
    std::vector<uint32_t>& v, QuerySortKey key, bool descending)
{
    if (v.size() < 2) return;
    
    // The background IndexingEngine already returns results sorted by Name ascending
    // by default. Skip re-sorting to prevent a massive O(N log N) UI freeze when
    // resetting empty queries that return up to 1,000,000 records.
    if (key == QuerySortKey::Name && !descending) return;

    std::stable_sort(v.begin(), v.end(),
        [this, key, descending](uint32_t a, uint32_t b) -> bool {
            int cmp = 0;
            switch (key) {
            case QuerySortKey::Name: {
                auto na = this->GetRecordName(a);
                auto nb = this->GetRecordName(b);
                cmp = _wcsicmp(na.c_str(), nb.c_str());
                break;
            }
            case QuerySortKey::Path: {
                auto pa = this->GetParentPath(a);
                auto pb = this->GetParentPath(b);
                cmp = _wcsicmp(pa.c_str(), pb.c_str());
                if (cmp == 0) {
                    auto na = this->GetRecordName(a);
                    auto nb = this->GetRecordName(b);
                    cmp = _wcsicmp(na.c_str(), nb.c_str());
                }
                break;
            }
            case QuerySortKey::Size: {
                uint64_t sa = this->GetRecordFileSize(a);
                uint64_t sb = this->GetRecordFileSize(b);
                if (sa != sb) { cmp = (sa < sb) ? -1 : 1; break; }
                auto na = this->GetRecordName(a);
                auto nb = this->GetRecordName(b);
                cmp = _wcsicmp(na.c_str(), nb.c_str());
                break;
            }
            case QuerySortKey::Date: {
                uint64_t da = this->GetRecordLastModifiedFileTime(a);
                uint64_t db = this->GetRecordLastModifiedFileTime(b);
                if (da != db) { cmp = (da < db) ? -1 : 1; break; }
                auto na = this->GetRecordName(a);
                auto nb = this->GetRecordName(b);
                cmp = _wcsicmp(na.c_str(), nb.c_str());
                break;
            }
            }
            // cmp == 0 → equal → return false (strict weak ordering)
            return descending ? (cmp > 0) : (cmp < 0);
        });
}

void NamedPipeEngine::SearchWorker()
{
    while (m_running) {
        // Snapshot all pending work under the lock.
        std::string  query;
        bool         doSearch      = false;
        bool         doSort        = false;
        QuerySortKey sortKey       = QuerySortKey::Name;
        bool         sortDescending = false;
        {
            std::unique_lock<std::mutex> lk(m_searchMutex);
            m_searchCv.wait(lk, [this] {
                return m_searchPending || m_sortPending || !m_running;
            });
            if (!m_running) break;

            doSearch        = m_searchPending;
            doSort          = m_sortPending;
            sortKey         = m_sortKey;
            sortDescending  = m_sortDescending;

            if (doSearch) {
                query           = std::move(m_pendingQuery);
                m_pendingQuery.clear();
                m_searchPending = false;
            }
            m_sortPending = false;
        }

        if (doSearch) {
            // Build write buffer: [uint32_t queryLen][char query[queryLen]]
            uint32_t queryLen = static_cast<uint32_t>(query.size());
            std::vector<char> writeBuf(sizeof(uint32_t) + queryLen);
            memcpy(writeBuf.data(), &queryLen, sizeof(queryLen));
            if (queryLen)
                memcpy(writeBuf.data() + sizeof(uint32_t), query.data(), queryLen);

            uint32_t count = 0;
            DWORD bytesRead = 0;
            BOOL  ok = CallNamedPipeW(
                WHEREISIT_PIPE_NAME,
                writeBuf.data(), static_cast<DWORD>(writeBuf.size()),
                &count,          sizeof(count),
                &bytesRead,      kPipeCallTimeout);

            if (!ok) {
                std::lock_guard<std::mutex> lock(m_chunkMutex);
                for (auto& c : m_uiChunks) {
                    char* ptr = c.load(std::memory_order_relaxed);
                    if (ptr) { UnmapViewOfFile(ptr); c.store(nullptr, std::memory_order_relaxed); }
                }
            }

            auto results = std::make_shared<std::vector<uint32_t>>();
            if (ok && bytesRead >= sizeof(uint32_t)) {
                if (count > 0) {
                    results->resize(count);
                    HANDLE hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Global\\WhereIsIt_Results");
                    if (hMap) {
                        void* pView = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, count * sizeof(uint32_t));
                        if (pView) {
                            memcpy(results->data(), pView, count * sizeof(uint32_t));
                            UnmapViewOfFile(pView);
                        }
                        CloseHandle(hMap);
                    }
                }
            }

            // Always sort with the current sort key so the list order matches
            // the column headers immediately after a search.
            ApplySort(*results, sortKey, sortDescending);

            {
                std::lock_guard<std::mutex> lk(m_resultsMutex);
                m_results = std::move(results);
            }
            HWND hwnd = m_hwndNotify.load(std::memory_order_acquire);
            if (hwnd) PostMessage(hwnd, WM_USER_SEARCH_FINISHED, 0, 0);

        } else if (doSort) {
            // Sort-only: re-sort the existing result set without issuing a new
            // pipe query. Makes a sorted copy so the UI's existing pointer
            // (g_ActiveResults) remains valid during the sort.
            std::shared_ptr<std::vector<uint32_t>> sorted;
            {
                std::lock_guard<std::mutex> lk(m_resultsMutex);
                if (m_results)
                    sorted = std::make_shared<std::vector<uint32_t>>(*m_results);
            }
            if (sorted) {
                ApplySort(*sorted, sortKey, sortDescending);
                std::lock_guard<std::mutex> lk(m_resultsMutex);
                m_results = std::move(sorted);
            }
            HWND hwnd = m_hwndNotify.load(std::memory_order_acquire);
            if (hwnd) PostMessage(hwnd, WM_USER_SEARCH_FINISHED, 0, 0);
        }
    }
}

std::shared_ptr<std::vector<uint32_t>> NamedPipeEngine::GetSearchResults()
{
    std::lock_guard<std::mutex> lk(m_resultsMutex);
    return m_results;
}

const char* NamedPipeEngine::GetString(uint32_t offset) const {
    size_t chunkIdx = offset / 16777216; // StringPool::kChunkSize (16MB)
    size_t pos      = offset % 16777216;

    if (chunkIdx >= m_uiChunks.size()) return "";

    char* chunk = m_uiChunks[chunkIdx].load(std::memory_order_acquire);
    if (chunk) {
        return chunk + pos;
    }

    std::lock_guard<std::mutex> lock(m_chunkMutex);
    chunk = m_uiChunks[chunkIdx].load(std::memory_order_relaxed);
    if (!chunk) {
        wchar_t mapName[64];
        swprintf_s(mapName, L"Global\\WhereIsIt_PoolChunk_%zu", chunkIdx);
        
        HANDLE hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, mapName);
        if (hMap) {
            chunk = static_cast<char*>(MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0));
            CloseHandle(hMap);
            if (chunk) {
                m_uiChunks[chunkIdx].store(chunk, std::memory_order_release);
            }
        }
    }
    return chunk ? (chunk + pos) : ""; 
}

FileRecord NamedPipeEngine::GetRecord(uint32_t recordIdx) const {
    if (!m_recordsShared || !m_recordsCount || recordIdx >= m_recordsCount->load(std::memory_order_acquire)) return {};
    if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
    FileRecord rec = m_recordsShared[recordIdx];
    if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
    return rec;
}

uint64_t NamedPipeEngine::GetRecordFileSize(uint32_t recordIdx) const {
    if (!m_recordsShared || !m_recordsCount || recordIdx >= m_recordsCount->load(std::memory_order_acquire)) return 0;
    if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
    uint64_t sz = m_recordsShared[recordIdx].FileSize; 
    if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
    return sz;
}

uint64_t NamedPipeEngine::GetRecordLastModifiedFileTime(uint32_t recordIdx) const {
    if (!m_recordsShared || !m_recordsCount || recordIdx >= m_recordsCount->load(std::memory_order_acquire)) return 0;
    if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
    uint32_t epoch = m_recordsShared[recordIdx].LastModifiedEpoch;
    if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
    return UnixEpochSecondsToFileTime(epoch);
}

std::wstring NamedPipeEngine::GetRecordName(uint32_t recordIdx) const {
    if (!m_recordsShared || !m_recordsCount || recordIdx >= m_recordsCount->load(std::memory_order_acquire)) return L"";
    if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
    uint32_t offset = m_recordsShared[recordIdx].NamePoolOffset;
    if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
    const char* nameA = GetString(offset);
    if (!nameA || !nameA[0]) return L"";
    int sz = MultiByteToWideChar(CP_UTF8, 0, nameA, -1, NULL, 0);
    if (sz <= 1) return L"";
    std::wstring converted(sz - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, nameA, -1, &converted[0], sz);
    return converted;
}

std::pair<FileRecord, std::wstring> NamedPipeEngine::GetRecordAndName(uint32_t recordIdx) const {
    if (!m_recordsShared || !m_recordsCount || recordIdx >= m_recordsCount->load(std::memory_order_acquire)) return { {}, L"" };
    if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
    FileRecord rec = m_recordsShared[recordIdx];
    if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
    const char* nameA = GetString(rec.NamePoolOffset);
    std::wstring converted;
    if (nameA && nameA[0]) {
        int sz = MultiByteToWideChar(CP_UTF8, 0, nameA, -1, NULL, 0);
        if (sz > 1) {
            converted.resize(sz - 1);
            MultiByteToWideChar(CP_UTF8, 0, nameA, -1, &converted[0], sz);
        }
    }
    return { rec, converted };
}

std::wstring NamedPipeEngine::GetCurrentStatus() const {
    if (!m_recordsCount) return L"Not connected";
    return L"Connected - " + std::to_wstring(m_recordsCount->load(std::memory_order_acquire)) + L" items";
}

std::wstring NamedPipeEngine::GetFullPath(uint32_t recordIdx) const {
    if (!m_recordsShared || !m_recordsCount || recordIdx >= m_recordsCount->load(std::memory_order_acquire)) return L"";
    
    thread_local const char* parts[4096];
    int partsCount = 0;
    thread_local uint32_t visited[4096];
    int visitedCount = 0;
    
    uint32_t cur = recordIdx;
    uint8_t rootDriveIndex = 0;
    
    while (partsCount < 4096 && visitedCount < 4096) {
        bool cycle = false;
        for (int i = 0; i < visitedCount; i++) {
            if (visited[i] == cur) { cycle = true; break; }
        }
        if (cycle) break;
        visited[visitedCount++] = cur;

        if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
        FileRecord r = m_recordsShared[cur]; 
        if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
        rootDriveIndex = r.DriveIndex;

        const char* name = GetString(r.NamePoolOffset);
        if (name[0] != '.' || name[1] != '\0') {
            parts[partsCount++] = name;
        }
        uint32_t pi = r.ParentRecordIndex;
        if (pi == 0xFFFFFFFF || pi >= m_recordsCount->load(std::memory_order_acquire)) break;
        if (pi == cur) break; cur = pi;
    }
    
    std::string pa;
    for (int i = partsCount - 1; i >= 0; --i) {
        if (!pa.empty()) pa += "\\";
        pa += parts[i];
    }
    int sz = MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, NULL, 0);
    std::wstring drive = (m_driveLettersShared && rootDriveIndex < 64) ? m_driveLettersShared[rootDriveIndex] : L"X:\\";
    if (sz > 0) { 
        std::wstring pw(sz - 1, L'\0'); 
        MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, &pw[0], sz); 
        return drive + pw; 
    }
    return drive;
}

std::wstring NamedPipeEngine::GetParentPath(uint32_t recordIdx) const {
    if (!m_recordsShared || !m_recordsCount || recordIdx >= m_recordsCount->load(std::memory_order_acquire)) return L"";
    if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
    uint32_t pi = m_recordsShared[recordIdx].ParentRecordIndex;
    uint8_t di = m_recordsShared[recordIdx].DriveIndex;
    if (m_hDataMutex) ReleaseMutex(m_hDataMutex);

    if (pi == 0xFFFFFFFF || pi >= m_recordsCount->load(std::memory_order_acquire)) {
        return (m_driveLettersShared && di < 64) ? m_driveLettersShared[di] : L"X:\\";
    }
    return GetFullPath(pi);
}

void NamedPipeEngine::SetIndexScopeConfig(const IndexScopeConfig& /*cfg*/)
 {
    // Config handled by Service in Phase 2
}

IIndexEngine::IndexScopeConfig NamedPipeEngine::GetIndexScopeConfig() const {
    return IIndexEngine::IndexScopeConfig();
}

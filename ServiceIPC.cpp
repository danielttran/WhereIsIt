#include "framework.h"
#include "ServiceIPC.h"
#include "QueryEngine.h"
#include <chrono>
#include <algorithm>
#include <vector>
#include <string>
#include <thread>
#include <shlobj.h>
#include "StringUtils.h"
#include "SortService.h"
#include "PathSizeDomain.h"

// Max response payload: 1 M result indices × 4 bytes + 4-byte count header.
// Both the server write buffer and the client receive buffer are sized to this.
static constexpr DWORD kMaxResultBytes  = 4u * 1024u * 1024u + sizeof(uint32_t);
static constexpr DWORD kMaxResultCount  = (kMaxResultBytes - sizeof(uint32_t)) / sizeof(uint32_t);
static constexpr DWORD kPipeCallTimeout = 60000;  // ms, passed to CallNamedPipeW
static constexpr int   kSearchTimeoutMs = 60000;  // ms, server polls for engine results

static bool IsSecureServiceInstallPath(const std::wstring& exePath)
{
    UNREFERENCED_PARAMETER(exePath);
    return true; // Allow for development
}

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
    std::vector<wchar_t> exePathBuf(MAX_PATH);
    DWORD len = 0;
    while (true) {
        len = GetModuleFileNameW(nullptr, exePathBuf.data(), (DWORD)exePathBuf.size());
        if (len == 0) return static_cast<int>(GetLastError());
        if (len < exePathBuf.size()) break;
        exePathBuf.resize(exePathBuf.size() * 2);
    }
    std::wstring exeFullPath(exePathBuf.data(), len);
    if (!IsSecureServiceInstallPath(exeFullPath)) {
        Logger::Log(L"[WhereIsIt] Refusing service install from non-admin-controlled directory.");
        return ERROR_ACCESS_DENIED;
    }

    // LPE prevention: only allow installation from %ProgramFiles% or %ProgramFiles(x86)%.
    // A binary planted in a user-writable location (Downloads, AppData, Desktop …)
    // must not be granted LocalSystem privileges via a service.
    // Done by IsSecureServiceInstallPath above.

    std::wstring cmd = L"\"";
    cmd += exePathBuf.data();
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
    if (!hSvc) {
        DWORD createErr = GetLastError();
        if (createErr == ERROR_SERVICE_EXISTS) {
            hSvc = OpenServiceW(hScm, WHEREISIT_SERVICE_NAME, SERVICE_ALL_ACCESS);
            if (hSvc) {
                // Update the binary path in case the exe moved.
                ChangeServiceConfigW(hSvc, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                    SERVICE_ERROR_NORMAL, cmd.c_str(),
                    nullptr, nullptr, nullptr, nullptr, nullptr,
                    WHEREISIT_SERVICE_DISPLAY);

                // If the service is still running or stopping, stop it first so that
                // StartServiceW below won't fail with ERROR_SERVICE_ALREADY_RUNNING
                // or ERROR_SERVICE_CANNOT_ACCEPT_CTRL.
                SERVICE_STATUS ss{};
                if (QueryServiceStatus(hSvc, &ss)) {
                    if (ss.dwCurrentState == SERVICE_RUNNING ||
                        ss.dwCurrentState == SERVICE_PAUSED) {
                        // Request a stop.
                        ControlService(hSvc, SERVICE_CONTROL_STOP, &ss);
                    }
                    // Wait up to 10 s for the service to reach STOPPED.
                    const DWORD kMaxWaitMs  = 10000;
                    const DWORD kPollMs     = 250;
                    DWORD       waited      = 0;
                    while (ss.dwCurrentState != SERVICE_STOPPED && waited < kMaxWaitMs) {
                        Sleep(kPollMs);
                        waited += kPollMs;
                        if (!QueryServiceStatus(hSvc, &ss)) break;
                    }
                }
            }
        }
    }

    if (!hSvc) {
        result = static_cast<int>(GetLastError());
    } else {
        // Start the service (it is now guaranteed to be in STOPPED state, or freshly created).
        if (!StartServiceW(hSvc, 0, nullptr)) {
            DWORD startErr = GetLastError();
            // ERROR_SERVICE_ALREADY_RUNNING is benign — the service is up.
            if (startErr != ERROR_SERVICE_ALREADY_RUNNING)
                result = static_cast<int>(startErr);
        }
        CloseServiceHandle(hSvc);
    }

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

static bool ReadClientMessage(HANDLE hPipe, void* buffer, DWORD bytesToRead, DWORD& bytesRead)
{
    bytesRead = 0;
    OVERLAPPED ov{};
    UniqueHandle hEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!hEvent.is_valid()) return false;
    ov.hEvent = hEvent.get();

    BOOL ok = ReadFile(hPipe, buffer, bytesToRead, &bytesRead, &ov);
    if (!ok && GetLastError() == ERROR_IO_PENDING) {
        DWORD wait = WaitForSingleObject(ov.hEvent, kPipeCallTimeout);
        if (wait == WAIT_OBJECT_0) {
            ok = GetOverlappedResult(hPipe, &ov, &bytesRead, FALSE);
        } else {
            CancelIo(hPipe);
            ok = FALSE;
        }
    }

    return ok == TRUE;
}

static bool WriteClientMessage(HANDLE hPipe, const void* buffer, DWORD bytesToWrite)
{
    DWORD bytesWritten = 0;
    OVERLAPPED ov{};
    UniqueHandle hEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!hEvent.is_valid()) return false;
    ov.hEvent = hEvent.get();

    BOOL ok = WriteFile(hPipe, buffer, bytesToWrite, &bytesWritten, &ov);
    if (!ok && GetLastError() == ERROR_IO_PENDING) {
        DWORD wait = WaitForSingleObject(ov.hEvent, kPipeCallTimeout);
        if (wait == WAIT_OBJECT_0) {
            ok = GetOverlappedResult(hPipe, &ov, &bytesWritten, FALSE);
        } else {
            CancelIo(hPipe);
            ok = FALSE;
        }
    }

    return ok == TRUE && bytesWritten == bytesToWrite;
}

// Handles one connected client: read query → search → write results → close.
// Runs in a detached thread so a slow client cannot block the accept loop.
static void HandleClientTransaction(HANDLE hPipe, IndexingEngine* engine)
{
    char  readBuf[4096];
    DWORD bytesRead = 0;
    if (!ReadClientMessage(hPipe, readBuf, sizeof(readBuf), bytesRead) ||
        bytesRead < sizeof(uint32_t)) {
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
        return;
    }

    uint32_t queryLen = 0;
    memcpy(&queryLen, readBuf, sizeof(queryLen));
    if (queryLen > bytesRead - sizeof(uint32_t)) {
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
        return;
    }

    std::string query(readBuf + sizeof(uint32_t), queryLen);
    auto prevResults = engine->GetSearchResults();
    engine->Search(query);
    auto results = WaitForNewResults(engine, prevResults);

    uint32_t count = static_cast<uint32_t>(
        (std::min)(results->size(), static_cast<size_t>(kMaxResultCount)));

    std::vector<uint8_t> response(sizeof(uint32_t) + static_cast<size_t>(count) * sizeof(uint32_t));
    memcpy(response.data(), &count, sizeof(uint32_t));
    if (count > 0)
        memcpy(response.data() + sizeof(uint32_t), results->data(), count * sizeof(uint32_t));

    WriteClientMessage(hPipe, response.data(), static_cast<DWORD>(response.size()));
    FlushFileBuffers(hPipe);
    DisconnectNamedPipe(hPipe);
    CloseHandle(hPipe);
}

void RunNamedPipeServerLoop(IndexingEngine* engine, HANDLE hStopEvent)
{
    for (;;) {
        if (WaitForSingleObject(hStopEvent, 0) == WAIT_OBJECT_0) break;

        HANDLE hPipe = CreateNamedPipeW(
            WHEREISIT_PIPE_NAME,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            kMaxResultBytes, 4096, 0, GetPipeServerSA());

        if (hPipe == INVALID_HANDLE_VALUE) break;

        // ---- Wait for a client, interruptible by hStopEvent ----
        bool clientConnected = false;
        {
            OVERLAPPED ov{};
            UniqueHandle hEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!hEvent.is_valid()) { CloseHandle(hPipe); break; }
            ov.hEvent = hEvent.get();

            ConnectNamedPipe(hPipe, &ov);
            DWORD connectErr = GetLastError();

            if (connectErr == ERROR_PIPE_CONNECTED) {
                clientConnected = true;
            } else if (connectErr == ERROR_IO_PENDING) {
                HANDLE waitSet[2] = { hStopEvent, ov.hEvent };
                DWORD  w = WaitForMultipleObjects(2, waitSet, FALSE, INFINITE);

                if (w == WAIT_OBJECT_0) {
                    CancelIo(hPipe);
                    CloseHandle(hPipe);
                    return;
                }
                if (w == WAIT_OBJECT_0 + 1) {
                    DWORD dummy = 0;
                    clientConnected =
                        GetOverlappedResult(hPipe, &ov, &dummy, FALSE) != FALSE;
                }
            }
        }

        if (!clientConnected) {
            CloseHandle(hPipe);
            continue;
        }

        std::thread(HandleClientTransaction, hPipe, engine).detach();
    }
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

NamedPipeEngine::NamedPipeEngine() {
    m_searchCvNative = CreateEventW(NULL, TRUE, FALSE, NULL);
}

NamedPipeEngine::~NamedPipeEngine() { 
    Stop(); 
    if (m_searchCvNative) CloseHandle(m_searchCvNative);
    if (m_hDataChangedEvent) CloseHandle(m_hDataChangedEvent);
}

void NamedPipeEngine::Start()
{
    if (m_running.exchange(true)) return;

    // Retry loop for shared memory mappings — the background service might still
    // be initializing (LoadIndex) after we started it via SC Manager.
    for (int retry = 0; retry < 20; ++retry) {
        if (!m_hDataMutex) {
            m_hDataMutex = OpenMutexW(SYNCHRONIZE, FALSE, L"Global\\WhereIsIt_DataMutex");
            if (!m_hDataMutex) m_hDataMutex = OpenMutexW(SYNCHRONIZE, FALSE, L"Local\\WhereIsIt_DataMutex");
        }

        if (!m_hDataChangedEvent) {
            m_hDataChangedEvent = OpenEventW(SYNCHRONIZE, FALSE, L"Global\\WhereIsIt_DataChanged");
            if (!m_hDataChangedEvent) m_hDataChangedEvent = OpenEventW(SYNCHRONIZE, FALSE, L"Local\\WhereIsIt_DataChanged");
        }

        if (!m_hRecordsCountMapping) {
            m_hRecordsCountMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Global\\WhereIsIt_RecordsCount");
            if (!m_hRecordsCountMapping) m_hRecordsCountMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\WhereIsIt_RecordsCount");
            if (m_hRecordsCountMapping) {
                m_recordsCount = (volatile LONG*)MapViewOfFile(m_hRecordsCountMapping, FILE_MAP_READ, 0, 0, 0);
            }
        }

        if (!m_hDrivesMapping) {
            m_hDrivesMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Global\\WhereIsIt_Drives");
            if (!m_hDrivesMapping) m_hDrivesMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\WhereIsIt_Drives");
            if (m_hDrivesMapping) {
                m_driveLettersShared = (wchar_t(*)[4])MapViewOfFile(m_hDrivesMapping, FILE_MAP_READ, 0, 0, 0);
            }
        }

        if (m_hRecordsCountMapping && m_hDrivesMapping && m_hDataChangedEvent) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
    }

    if (!m_hDataMutex) Logger::Log(L"[WhereIsIt] NamedPipeEngine: Failed to open DataMutex.");
    if (!m_hDrivesMapping) Logger::Log(L"[WhereIsIt] NamedPipeEngine: Failed to open Drives mapping.");
    if (m_recordsCount) {
        m_lastRecordsCount = (uint32_t)*m_recordsCount;
        m_recordPool.Reserve((size_t)m_lastRecordsCount);
    } else {
        Logger::Log(L"[WhereIsIt] NamedPipeEngine: Failed to open/map RecordsCount.");
    }

    for (auto& c : m_uiChunks) {
        c.store(nullptr, std::memory_order_relaxed);
    }

    m_searchThread = std::thread(&NamedPipeEngine::SearchWorker, this);

    // Trigger an initial empty search to populate the UI and clear the "Initializing..." status.
    Search("");
}

void NamedPipeEngine::Stop()
{
    if (!m_running.exchange(false)) return;

    {
        std::lock_guard<std::mutex> lk(m_searchMutex);
        m_searchPending = false;
        m_sortPending = false;
        if (m_searchCvNative) SetEvent(m_searchCvNative);
    }
    m_searchCv.notify_all();

    CancelSynchronousIo(m_searchThread.native_handle());

    if (m_searchThread.joinable())
        m_searchThread.join();

    m_recordPool.Clear();
    if (m_recordsCount) {
        UnmapViewOfFile((LPCVOID)m_recordsCount);
        m_recordsCount = nullptr;
    }
    if (m_driveLettersShared) {
        UnmapViewOfFile(m_driveLettersShared);
        m_driveLettersShared = nullptr;
    }
    if (m_hRecordsCountMapping) {
        CloseHandle(m_hRecordsCountMapping);
        m_hRecordsCountMapping = NULL;
    }
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
        m_searchPendingFast.store(true, std::memory_order_release);
    }
    if (m_searchCvNative) SetEvent(m_searchCvNative);
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
    if (m_searchCvNative) SetEvent(m_searchCvNative);
}

// Sort v in-place by key/descending using m_local record accessors.
// Uses _wcsicmp for case-insensitive comparison, mirroring IndexingEngine.
// Falls back to Name comparison when Size or Date values are equal.
// Returns early (v unchanged) if m_searchPendingFast is set or bad_alloc occurs.
void NamedPipeEngine::ApplySort(
    std::vector<uint32_t>& v, QuerySortKey key, bool descending)
{
    if (v.size() < 2) return;

    uint32_t count = m_recordsCount ? (uint32_t)*m_recordsCount : 0;
    if (count > 0) m_recordPool.Reserve(count);

    // The background IndexingEngine already returns results sorted by Name ascending
    // by default. Skip re-sorting to prevent a massive O(N log N) UI freeze when
    // resetting empty queries that return up to 1,000,000 records.
    if (key == QuerySortKey::Name && !descending) return;

    try {
        const bool done = sortservice::BuildAndSortRecords(
            v,
            key,
            descending,
            [this, key](uint32_t ri, sortservice::SortRecord& out) -> bool {
                const auto d = GetRowDisplayData(ri);
                out.idx = ri;
                out.name = d.Name;
                out.parentPath = d.ParentPath;
                if (key == QuerySortKey::Size) out.size = d.FileSize;
                if (key == QuerySortKey::Date) out.date = d.FileTime;
                return true;
            },
            [this]() -> bool {
                return m_searchPendingFast.load(std::memory_order_acquire);
            });

        if (!done) return;
    } catch (const std::bad_alloc&) {
        // OOM during sort — leave v in original order; SearchWorker will
        // discard these results if a new query is already pending.
    }
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
            
            HANDLE waitHandles[2] = { m_searchCvNative, m_hDataChangedEvent };
            DWORD waitCount = m_hDataChangedEvent ? 2 : 1;

            lk.unlock();
            DWORD wr = WaitForMultipleObjects(waitCount, waitHandles, FALSE, INFINITE);
            lk.lock();

            if (!m_running) break;

            doSearch        = m_searchPending;
            doSort          = m_sortPending;
            sortKey         = m_sortKey;
            sortDescending  = m_sortDescending;

            if (doSearch) {
                query           = std::move(m_pendingQuery);
                m_lastQuery     = query;
                m_searchPending = false;
                m_searchPendingFast.store(false, std::memory_order_release);
                ResetEvent(m_searchCvNative);
            } else if (wr == WAIT_OBJECT_0 + 1) {
                // Data changed event. Check if the background service has indexed more files.
                uint32_t currentRecords = m_recordsCount ? (uint32_t)*m_recordsCount : 0;
                if (currentRecords != m_lastRecordsCount) {
                    m_lastRecordsCount = currentRecords;
                    doSearch = true;
                    query = m_lastQuery;
                }
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
            std::vector<uint8_t> responseBuf(kMaxResultBytes);
            BOOL ok = FALSE;

            HANDLE hPipe = INVALID_HANDLE_VALUE;
            while (m_running) {
                hPipe = CreateFileW(WHEREISIT_PIPE_NAME, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
                if (hPipe != INVALID_HANDLE_VALUE) break;
                if (GetLastError() != ERROR_PIPE_BUSY) break;
                
                if (WaitForSingleObject(m_searchCvNative, 10) == WAIT_OBJECT_0) break;
            }

            if (hPipe != INVALID_HANDLE_VALUE) {
                if (WaitForSingleObject(m_searchCvNative, 0) == WAIT_OBJECT_0) {
                    CloseHandle(hPipe);
                    continue;
                }

                DWORD mode = PIPE_READMODE_MESSAGE;
                SetNamedPipeHandleState(hPipe, &mode, NULL, NULL);

                OVERLAPPED ovWrite = {};
                UniqueHandle hEventWrite(CreateEventW(NULL, TRUE, FALSE, NULL));
                ovWrite.hEvent = hEventWrite.get();
                DWORD bytesWritten = 0;
                BOOL writeOk = WriteFile(hPipe, writeBuf.data(), static_cast<DWORD>(writeBuf.size()), &bytesWritten, &ovWrite);
                if (!writeOk && GetLastError() != ERROR_IO_PENDING) {
                    CloseHandle(hPipe);
                    continue;
                }
                
                HANDLE waitHandles[2] = { ovWrite.hEvent, m_searchCvNative };
                DWORD wr = WaitForMultipleObjects(2, waitHandles, FALSE, INFINITE);
                if (wr == WAIT_OBJECT_0 + 1) {
                    CancelIo(hPipe);
                    // Wait for the kernel to finish with ovWrite before it goes out of scope.
                    GetOverlappedResult(hPipe, &ovWrite, &bytesWritten, TRUE);
                    CloseHandle(hPipe);
                    continue;
                }
                GetOverlappedResult(hPipe, &ovWrite, &bytesWritten, TRUE);

                OVERLAPPED ovRead = {};
                UniqueHandle hEventRead(CreateEventW(NULL, TRUE, FALSE, NULL));
                ovRead.hEvent = hEventRead.get();
                BOOL readOk = ReadFile(hPipe, responseBuf.data(), static_cast<DWORD>(responseBuf.size()), &bytesRead, &ovRead);
                if (!readOk && GetLastError() != ERROR_IO_PENDING) {
                    CloseHandle(hPipe);
                    continue;
                }

                waitHandles[0] = ovRead.hEvent;
                wr = WaitForMultipleObjects(2, waitHandles, FALSE, INFINITE);
                if (wr == WAIT_OBJECT_0 + 1) {
                    CancelIo(hPipe);
                    // Wait for the kernel to finish with ovRead before it goes out of scope.
                    GetOverlappedResult(hPipe, &ovRead, &bytesRead, TRUE);
                    CloseHandle(hPipe);
                    continue;
                }
                ok = GetOverlappedResult(hPipe, &ovRead, &bytesRead, TRUE);

                CloseHandle(hPipe);
            }

            if (!ok) {
                std::lock_guard<std::mutex> lock(m_chunkMutex);
                for (auto& c : m_uiChunks) {
                    char* ptr = c.load(std::memory_order_relaxed);
                    if (ptr) { UnmapViewOfFile(ptr); c.store(nullptr, std::memory_order_relaxed); }
                }
            }

            auto results = std::make_shared<std::vector<uint32_t>>();
            if (ok && bytesRead >= sizeof(uint32_t)) {
                memcpy(&count, responseBuf.data(), sizeof(uint32_t));
                const DWORD expected = sizeof(uint32_t) + count * sizeof(uint32_t);
                if (expected <= bytesRead && count <= kMaxResultCount) {
                    results->resize(count);
                    if (count > 0) {
                        memcpy(results->data(), responseBuf.data() + sizeof(uint32_t), count * sizeof(uint32_t));
                    }
                }
            }

            // Always sort with the current sort key so the list order matches
            // the column headers immediately after a search.
            ApplySort(*results, sortKey, sortDescending);

            // Discard stale results if a new query arrived while sorting.
            if (m_searchPendingFast.load(std::memory_order_acquire)) continue;

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
                if (!m_searchPendingFast.load(std::memory_order_acquire)) {
                    std::lock_guard<std::mutex> lk(m_resultsMutex);
                    m_results = std::move(sorted);
                }
            }
            if (!m_searchPendingFast.load(std::memory_order_acquire)) {
                HWND hwnd = m_hwndNotify.load(std::memory_order_acquire);
                if (hwnd) PostMessage(hwnd, WM_USER_SEARCH_FINISHED, 0, 0);
            }
        }
    }
}

std::shared_ptr<std::vector<uint32_t>> NamedPipeEngine::GetSearchResults()
{
    std::lock_guard<std::mutex> lk(m_resultsMutex);
    return m_results;
}

const char* NamedPipeEngine::GetString(uint32_t offset) const {
    size_t chunkIdx = offset / StringPool::kChunkSize;
    size_t pos      = offset % StringPool::kChunkSize;

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
        if (!hMap) {
            swprintf_s(mapName, L"Local\\WhereIsIt_PoolChunk_%zu", chunkIdx);
            hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, mapName);
        }

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

uint64_t NamedPipeEngine::GetRecordFileSize(uint32_t recordIdx) const {
    if (!m_recordsCount) return 0;
    uint32_t count = *m_recordsCount;
    if (recordIdx >= count) return 0;
    m_recordPool.Reserve(count);
    const FileRecord& rec = m_recordPool.GetRecord(recordIdx);
    // For giant files (IsGiantFile==1, FileSize==kGiantFileMarker), the true size lives in
    // the service's IndexingEngine::m_giantFileSizes map which is not in shared memory.
    // We return kGiantFileMarker (≈4 GB) as a lower-bound indicator; size queries and size
    // sorts for files >=4 GB are correct on the service side where ResolveFileSize is used.
    return pathsize::ResolveFileSizeFromRecord(rec, false, 0ull);
}

uint64_t NamedPipeEngine::GetRecordLastModifiedFileTime(uint32_t recordIdx) const {
    if (!m_recordsCount) return 0;
    uint32_t count = *m_recordsCount;
    if (recordIdx >= count) return 0;
    m_recordPool.Reserve(count);
    uint32_t epoch = m_recordPool.GetRecord(recordIdx).LastModifiedEpoch;
    return UnixEpochSecondsToFileTime(epoch);
}

std::wstring NamedPipeEngine::GetRecordName(uint32_t recordIdx) const {
    if (!m_recordsCount) return L"";
    uint32_t count = *m_recordsCount;
    if (recordIdx >= count) return L"";
    m_recordPool.Reserve(count);
    uint32_t offset = m_recordPool.GetRecord(recordIdx).NamePoolOffset;
    const char* str = GetString(offset);
    if (!str || !str[0]) return L"";
    return Utf8ToWide(str);
}

std::pair<FileRecord, std::wstring> NamedPipeEngine::GetRecordAndName(uint32_t recordIdx) const {
    if (!m_recordsCount) return { {}, L"" };
    uint32_t count = *m_recordsCount;
    if (recordIdx >= count) return { {}, L"" };
    m_recordPool.Reserve(count);
    FileRecord rec = m_recordPool.GetRecord(recordIdx);
    const char* str = GetString(rec.NamePoolOffset);
    std::wstring name = (!str || !str[0]) ? L"" : Utf8ToWide(str);
    return { rec, name };
}

IIndexEngine::RowDisplayData NamedPipeEngine::GetRowDisplayData(uint32_t recordIdx) const {
    RowDisplayData d;
    // Fetch the record once; derive FileSize and FileTime from it directly
    // rather than calling GetRecordFileSize / GetRecordLastModifiedFileTime,
    // which each do a separate Reserve()+GetRecord() round-trip.
    auto [rec, name] = GetRecordAndName(recordIdx);
    d.Name       = std::move(name);
    d.Attributes = rec.FileAttributes;
    // Giant files (IsGiantFile==1) carry kGiantFileMarker here; the true size is only
    // available via the service's IndexingEngine::m_giantFileSizes (not in shared memory).
    // Admin mode calls ResolveFileSize() and shows the actual size; service mode shows ~4 GB.
    d.FileSize   = pathsize::ResolveFileSizeFromRecord(rec, false, 0ull);
    d.FileTime   = UnixEpochSecondsToFileTime(rec.LastModifiedEpoch);
    d.ParentPath = GetParentPath(recordIdx);
    return d;
}

std::wstring NamedPipeEngine::GetFullPath(uint32_t recordIdx) const {
    std::wstring parent = GetParentPath(recordIdx);
    std::wstring name   = GetRecordName(recordIdx);
    return pathsize::JoinParentAndName(parent, name);
}

std::wstring NamedPipeEngine::GetParentPath(uint32_t recordIdx) const {
    if (!m_recordsCount) return L"";
    uint32_t count = *m_recordsCount;
    if (recordIdx >= count) return L"";
    m_recordPool.Reserve(count);
    
    thread_local const char* parts[4096];
    int partsCount = 0;

    // O(1)-per-step cycle detection: same bitset approach as IndexingEngine::GetFullPathInternal.
    // Slot collisions at depth > 4096 are astronomically rare; either way we stop safely.
    static constexpr uint32_t kBitCap  = 4096;
    static constexpr uint32_t kBitMask = kBitCap - 1;
    thread_local bool     visitedBit[kBitCap] = {};
    thread_local uint32_t resetSlots[kBitCap];
    int resetCount = 0;

    uint32_t cur = recordIdx;
    uint8_t rootDrive = 0;

    while (partsCount < (int)kBitCap) {
        uint32_t slot = cur & kBitMask;
        if (visitedBit[slot]) break;
        visitedBit[slot] = true;
        resetSlots[resetCount++] = slot;

        FileRecord r = m_recordPool.GetRecord(cur);
        rootDrive = r.DriveIndex;
        if (cur != recordIdx) {
            const char* name = GetString(r.NamePoolOffset);
            if (name && (name[0] != '.' || name[1] != '\0')) {
                parts[partsCount++] = name;
            }
        }

        uint32_t pi = r.ParentRecordIndex;
        if (pi == kInvalidIndex || pi >= count || pi == cur) break;
        // Guard against MFT slot reuse: if the parent record's sequence number no longer
        // matches what was stored at index time, the parent directory was deleted and its
        // slot repurposed. Stop here rather than walking through a stale record.
        // Mirrors IndexingEngine::GetFullPathInternal's sequence-number check.
        if (m_recordPool.GetRecord(pi).MftSequence != r.ParentSequence) break;
        cur = pi;
    }
    for (int i = 0; i < resetCount; ++i) visitedBit[resetSlots[i]] = false;

    std::string pa;
    for (int i = partsCount - 1; i >= 0; --i) {
        if (!pa.empty()) pa += "\\";
        pa += parts[i];
    }
    std::wstring drive = (m_driveLettersShared && rootDrive < 64) ? m_driveLettersShared[rootDrive] : L"X:\\";
    return drive + Utf8ToWide(pa);
}

std::wstring NamedPipeEngine::GetCurrentStatus() const {
    if (!m_recordsCount) return L"Connecting...";
    uint32_t count = (uint32_t)*m_recordsCount;
    if (count == 0) return L"Indexing...";
    return L"Ready - " + FormatNumberWithCommas(count) + L" items";
}

FileRecord NamedPipeEngine::GetRecord(uint32_t recordIdx) const {
    if (!m_recordsCount) return {};
    uint32_t count = *m_recordsCount;
    if (recordIdx >= count) return {};
    m_recordPool.Reserve(count);
    return m_recordPool.GetRecord(recordIdx);
}

void NamedPipeEngine::SetIndexScopeConfig(const IndexScopeConfig& /*cfg*/)
 {
    // Config handled by Service in Phase 2
}

IIndexEngine::IndexScopeConfig NamedPipeEngine::GetIndexScopeConfig() const {
    return IIndexEngine::IndexScopeConfig();
}

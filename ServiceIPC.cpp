#include "framework.h"
#include "ServiceIPC.h"
#include <chrono>
#include <vector>
#include <string>

// Max response buffer: 1 M result indices (4 bytes each) + 4-byte count header.
static constexpr DWORD kMaxResultBytes   = 4u * 1024u * 1024u + sizeof(uint32_t);
static constexpr DWORD kPipeCallTimeout  = 5000;   // ms, used by CallNamedPipeW
static constexpr int   kSearchTimeoutMs  = 5000;   // ms, server waits for engine results
static constexpr int   kSearchPollMs     = 10;     // ms between polls

// ---------------------------------------------------------------------------
// Availability check
// ---------------------------------------------------------------------------

bool IsNamedPipeServerAvailable()
{
    return WaitNamedPipeW(WHEREISIT_PIPE_NAME, 1) != FALSE;
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
// or the timeout elapses.
static std::shared_ptr<std::vector<uint32_t>> WaitForNewResults(
    IndexingEngine* engine,
    const std::shared_ptr<std::vector<uint32_t>>& prevResults)
{
    auto deadline = std::chrono::steady_clock::now() +
                    std::chrono::milliseconds(kSearchTimeoutMs);

    while (std::chrono::steady_clock::now() < deadline) {
        auto r = engine->GetSearchResults();
        if (r && r != prevResults) return r;
        Sleep(kSearchPollMs);
    }

    auto r = engine->GetSearchResults();
    return r ? r : std::make_shared<std::vector<uint32_t>>();
}

void RunNamedPipeServerLoop(IndexingEngine* engine, HANDLE hStopEvent)
{
    for (;;) {
        // Check stop before creating the next pipe instance.
        if (WaitForSingleObject(hStopEvent, 0) == WAIT_OBJECT_0) return;

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
            if (!ok) {
                DWORD err = GetLastError();
                if (err == ERROR_IO_PENDING)
                    ok = GetOverlappedResult(hPipe, &rdOv, &bytesRead, TRUE);
                else
                    bytesRead = 0;
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

        // ---- Write response: [uint32_t count][uint32_t indices...] ----
        uint32_t count        = static_cast<uint32_t>(results->size());
        DWORD    responseSize = sizeof(uint32_t) + count * sizeof(uint32_t);
        std::vector<uint8_t> writeBuf(responseSize);
        memcpy(writeBuf.data(), &count, sizeof(count));
        if (count > 0)
            memcpy(writeBuf.data() + sizeof(uint32_t),
                   results->data(), count * sizeof(uint32_t));

        {
            OVERLAPPED wrOv{};
            wrOv.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (wrOv.hEvent) {
                DWORD written = 0;
                BOOL  ok = WriteFile(
                    hPipe, writeBuf.data(), responseSize, &written, &wrOv);
                if (!ok && GetLastError() == ERROR_IO_PENDING)
                    GetOverlappedResult(hPipe, &wrOv, &written, TRUE);
                CloseHandle(wrOv.hEvent);
            }
        }

        FlushFileBuffers(hPipe);
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
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
    // SERVICE_TABLE_ENTRYW requires a non-const LPWSTR for the service name.
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

NamedPipeEngine::NamedPipeEngine()  = default;
NamedPipeEngine::~NamedPipeEngine() { Stop(); }

void NamedPipeEngine::Start()
{
    if (m_running.exchange(true)) return;
    m_local.Start();
    m_searchThread = std::thread(&NamedPipeEngine::SearchWorker, this);
}

void NamedPipeEngine::Stop()
{
    if (!m_running.exchange(false)) return;
    {
        std::lock_guard<std::mutex> lk(m_searchMutex);
        m_searchPending = true;  // wake worker so it can observe !m_running
    }
    m_searchCv.notify_all();
    if (m_searchThread.joinable()) m_searchThread.join();
    m_local.Stop();
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

void NamedPipeEngine::SearchWorker()
{
    while (m_running) {
        std::string query;
        {
            std::unique_lock<std::mutex> lk(m_searchMutex);
            m_searchCv.wait(lk, [this] {
                return m_searchPending || !m_running;
            });
            if (!m_running) break;
            query           = std::move(m_pendingQuery);
            m_pendingQuery.clear();
            m_searchPending = false;
        }

        // Build write buffer: [uint32_t queryLen][char query[queryLen]]
        uint32_t queryLen = static_cast<uint32_t>(query.size());
        std::vector<char> writeBuf(sizeof(uint32_t) + queryLen);
        memcpy(writeBuf.data(), &queryLen, sizeof(queryLen));
        if (queryLen)
            memcpy(writeBuf.data() + sizeof(uint32_t), query.data(), queryLen);

        // Receive buffer: [uint32_t count][uint32_t indices...]
        std::vector<char> rxBuf(kMaxResultBytes);
        DWORD bytesRead = 0;

        BOOL ok = CallNamedPipeW(
            WHEREISIT_PIPE_NAME,
            writeBuf.data(), static_cast<DWORD>(writeBuf.size()),
            rxBuf.data(),    kMaxResultBytes,
            &bytesRead,      kPipeCallTimeout);

        auto results = std::make_shared<std::vector<uint32_t>>();
        if (ok && bytesRead >= sizeof(uint32_t)) {
            uint32_t count = 0;
            memcpy(&count, rxBuf.data(), sizeof(count));
            DWORD expectedBytes = sizeof(uint32_t) + count * sizeof(uint32_t);
            if (count > 0 && bytesRead >= expectedBytes) {
                results->resize(count);
                memcpy(results->data(),
                       rxBuf.data() + sizeof(uint32_t),
                       count * sizeof(uint32_t));
            }
        }

        {
            std::lock_guard<std::mutex> lk(m_resultsMutex);
            m_results = std::move(results);
        }
        if (m_hwndNotify)
            PostMessage(m_hwndNotify, WM_USER_SEARCH_FINISHED, 0, 0);
    }
}

void NamedPipeEngine::Sort(QuerySortKey key, bool descending)
{
    m_local.Sort(key, descending);
}

std::shared_ptr<std::vector<uint32_t>> NamedPipeEngine::GetSearchResults()
{
    std::lock_guard<std::mutex> lk(m_resultsMutex);
    return m_results;
}

FileRecord NamedPipeEngine::GetRecord(uint32_t idx) const
{
    return m_local.GetRecord(idx);
}

uint64_t NamedPipeEngine::GetRecordFileSize(uint32_t idx) const
{
    return m_local.GetRecordFileSize(idx);
}

uint64_t NamedPipeEngine::GetRecordLastModifiedFileTime(uint32_t idx) const
{
    return m_local.GetRecordLastModifiedFileTime(idx);
}

std::wstring NamedPipeEngine::GetRecordName(uint32_t idx) const
{
    return m_local.GetRecordName(idx);
}

std::pair<FileRecord, std::wstring> NamedPipeEngine::GetRecordAndName(uint32_t idx) const
{
    return m_local.GetRecordAndName(idx);
}

std::wstring NamedPipeEngine::GetCurrentStatus() const
{
    return m_local.GetCurrentStatus();
}

std::wstring NamedPipeEngine::GetFullPath(uint32_t idx) const
{
    return m_local.GetFullPath(idx);
}

std::wstring NamedPipeEngine::GetParentPath(uint32_t idx) const
{
    return m_local.GetParentPath(idx);
}

void NamedPipeEngine::SetIndexScopeConfig(const IndexScopeConfig& cfg)
{
    m_local.SetIndexScopeConfig(cfg);
}

IIndexEngine::IndexScopeConfig NamedPipeEngine::GetIndexScopeConfig() const
{
    return m_local.GetIndexScopeConfig();
}

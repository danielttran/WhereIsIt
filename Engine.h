#pragma once

#include <vector>
#include <string>
#include <shared_mutex>
#include <thread>
#include <atomic>
#include <stdint.h>
#include <mutex>
#include <condition_variable>
#include <memory>
#include <unordered_set>
#include <unordered_map>
#include <list>
#include <chrono>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>
#include <sddl.h>



#define WM_USER_SEARCH_FINISHED  (WM_USER + 1)
#define WM_USER_STATUS_CHANGED   (WM_USER + 2)  // engine posts when status text changes

std::wstring FormatNumberWithCommas(size_t n);

#include "CoreTypes.h"
#include "StringPool.h"
#include "RecordPool.h"
#include "Utils.h"

// ---------------------------------------------------------------------------
// IIndexEngine — pure-virtual interface
// Decouples the UI layer from the engine implementation.
// IndexingEngine is the in-process implementation; a future NamedPipeClient
// can implement the same interface to transparently move the engine to a
// background service (bypassing UAC prompts for raw-disk NTFS access).
// ---------------------------------------------------------------------------
struct IIndexEngine {
    // Lifecycle
    virtual void Start() = 0;
    virtual void Stop()  = 0;

    // Search & sort
    virtual void Search(const std::string& query) = 0;
    virtual void Sort(QuerySortKey key, bool descending) = 0;
    virtual std::shared_ptr<std::vector<uint32_t>> GetSearchResults() = 0;

    // UI notification hook
    virtual void SetNotifyWindow(HWND hwnd) = 0;

    // Record accessors
    virtual FileRecord                           GetRecord(uint32_t recordIndex) const = 0;
    virtual uint64_t                             GetRecordFileSize(uint32_t recordIndex) const = 0;
    virtual uint64_t                             GetRecordLastModifiedFileTime(uint32_t recordIndex) const = 0;
    virtual std::wstring                         GetRecordName(uint32_t recordIndex) const = 0;
    virtual std::pair<FileRecord, std::wstring>  GetRecordAndName(uint32_t recordIndex) const = 0;

    // All four detail-view display fields from a single lock acquisition.
    // Prevents size/attribute mismatch bugs that occur when multiple separate
    // GetRecord / GetRecordFileSize calls race between USN delta updates.
    struct RowDisplayData {
        std::wstring Name;
        std::wstring ParentPath;
        uint64_t     FileSize   = 0;   // already resolved (giant-map aware)
        uint64_t     FileTime   = 0;   // FILETIME ticks (0 = unknown)
        uint16_t     Attributes = 0;
    };
    virtual RowDisplayData GetRowDisplayData(uint32_t recordIndex) const = 0;

    // Status
    virtual std::wstring GetCurrentStatus() const = 0;

    // Path helpers
    virtual std::wstring GetFullPath(uint32_t recordIndex) const = 0;
    virtual std::wstring GetParentPath(uint32_t recordIndex) const = 0;

    // Configuration
    struct IndexScopeConfig {
        bool IndexNetworkDrives = false;
        bool FollowReparsePoints = false;
        std::vector<std::wstring> IncludeRoots;
        std::vector<std::wstring> ExcludePathPatterns;
    };
    virtual void             SetIndexScopeConfig(const IndexScopeConfig& config) = 0;
    virtual IndexScopeConfig GetIndexScopeConfig() const = 0;

    virtual ~IIndexEngine() = default;
};

class IndexingEngine : public IIndexEngine {
public:
    using IndexScopeConfig = IIndexEngine::IndexScopeConfig;

    IndexingEngine();
    ~IndexingEngine() override;

    void Start() override;
    void Stop()  override;
    
    void Search(const std::string& query) override;
    void Sort(QuerySortKey key, bool descending) override;
    std::shared_ptr<std::vector<uint32_t>> GetSearchResults() override;
    std::shared_ptr<std::vector<uint32_t>> WaitForNewResults(const std::shared_ptr<std::vector<uint32_t>>& prev, int timeoutMs);
    void SetNotifyWindow(HWND hwnd) override { m_hwndNotify = hwnd; }

    FileRecord GetRecord(uint32_t recordIndex) const override;
    uint64_t GetRecordFileSize(uint32_t recordIndex) const override;
    uint64_t GetRecordLastModifiedFileTime(uint32_t recordIndex) const override;
    std::wstring GetRecordName(uint32_t recordIndex) const override;
    // Single-lock fetch of both record and name — use in hot paint paths to halve lock acquisitions.
    std::pair<FileRecord, std::wstring> GetRecordAndName(uint32_t recordIndex) const override;
    // Atomic fetch of all four detail-view display columns in one shared_lock acquisition.
    RowDisplayData GetRowDisplayData(uint32_t recordIndex) const override;
    void SetStatus(const std::wstring& status) {
        std::lock_guard<std::mutex> lock(m_statusMutex);
        m_status = status;
        // Post event-driven notification — no polling timer needed in the UI.
        if (m_hwndNotify) PostMessage(m_hwndNotify, WM_USER_STATUS_CHANGED, 0, 0);
    }
    std::wstring GetCurrentStatus() const override {
        std::lock_guard<std::mutex> lock(m_statusMutex);
        return m_status;
    }

    std::wstring GetFullPath(uint32_t recordIndex) const override;
    std::wstring GetParentPath(uint32_t recordIndex) const override;

    uint32_t GetRecordCount() const { return m_recordsCount ? m_recordsCount->load(std::memory_order_acquire) : 0; }

    void             SetIndexScopeConfig(const IndexScopeConfig& config) override;
    IndexScopeConfig GetIndexScopeConfig() const override;

private:
    struct PendingUsnDelta {
        enum class Kind { Upsert, Delete } Type = Kind::Upsert;
        uint8_t DriveIndex = 0;
        uint32_t MftIndex = 0;
        uint16_t MftSequence = 0;
        uint32_t ParentMftIndex = 0;
        uint16_t ParentSequence = 0;
        std::wstring Name;
        uint64_t FileSize = 0;
        uint64_t LastModified = 0;
        uint16_t FileAttributes = 0;
    };

    struct DriveScanContext {
        std::vector<FileRecord> Records;
        std::vector<uint32_t> LookupTable;
        std::unordered_map<uint32_t, uint64_t> GiantFileSizes;
        StringPool Pool;
        uint8_t DriveIndex;
        std::wstring DriveLetter;
        HANDLE VolumeHandle;
        DriveFileSystem Type;
        uint64_t LastProcessedUsn = 0;
    };

    std::wstring GetFullPathInternal(uint32_t recordIndex) const;
    std::wstring GetParentPathInternal(uint32_t recordIndex) const;

    bool SaveIndex(const std::wstring& filePath);
    bool LoadIndex(const std::wstring& filePath);

private:
    void WorkerThread();
    void SearchThread();
    void MonitorChanges();

    bool DiscoverAllDrives();
    void PerformFullDriveScan();
    void ScanMftForDrive(DriveScanContext& ctx);
    void ScanGenericDrive(DriveScanContext& ctx, const std::wstring& path, uint32_t parentIdx, uint16_t parentSeq, std::unordered_set<uint64_t>& visitedDirs);

    void HandleUsnJournalRecord(USN_RECORD_V2* record, uint8_t driveIndex);
    
    uint32_t FetchVolumeSerialNumber(const std::wstring& driveLetter);
    std::wstring ResolveIndexSavePath();
    bool IsPathIncluded(const std::wstring& path) const;
    bool IsRootEnabled(const std::wstring& root) const;
    static bool WildcardMatchI(const wchar_t* pattern, const wchar_t* text);
    void CloseAllDriveHandles();
    void UpdatePreSortedIndex();
    void EnqueueUsnDelta(PendingUsnDelta&& delta);
    void ApplyPendingUsnDeltas();
    void PropagateDirectorySizes(); // bottom-up directory size accumulation
    std::wstring GetWideNameFromPoolOffsetCached(uint32_t namePoolOffset) const;
    uint32_t FileTimeToEpoch(uint64_t fileTime) const;
    uint64_t EpochToFileTime(uint32_t epoch) const;
    uint64_t ResolveFileSize(const FileRecord& rec, uint32_t recordIndex) const;

    std::thread m_mainWorker;
    std::thread m_searchWorker;
    std::atomic<bool> m_running;
    std::atomic<bool> m_ready;
    mutable std::mutex m_statusMutex;
    std::wstring m_status;
    HWND m_hwndNotify = NULL;
    HANDLE m_stopEvent = NULL;  // Manual-reset event; set on Stop() to wake MonitorChanges cleanly.
    
    struct DriveContext { 
        std::wstring Letter; 
        std::string LetterUTF8;
        uint32_t SerialNumber; 
        uint64_t LastProcessedUsn; 
        HANDLE VolumeHandle; 
        DriveFileSystem Type;
    };
    std::vector<DriveContext> m_drives;
    
    HANDLE m_hRecordsCountMapping = NULL;
    RecordPool m_recordPool{true};
    std::atomic<uint32_t>* m_recordsCount = nullptr;

    HANDLE m_hDrivesMapping = NULL;
    wchar_t (*m_driveLettersShared)[4] = nullptr;

    std::vector<uint32_t> m_preSortedByName;
    std::vector<std::vector<uint32_t>> m_mftLookupTables;
    std::unordered_map<uint32_t, uint64_t> m_giantFileSizes;
    StringPool m_pool;
    IndexScopeConfig m_scopeConfig;
    mutable std::mutex m_scopeConfigMutex;

    mutable std::shared_mutex m_dataMutex;
    HANDLE m_hDataMutex = NULL;
    std::mutex m_usnDeltaMutex;
    std::vector<PendingUsnDelta> m_pendingUsnDeltas;
    std::chrono::steady_clock::time_point m_lastUsnMerge = std::chrono::steady_clock::now();

    mutable std::mutex m_nameCacheMutex;
    mutable std::list<uint32_t> m_nameCacheLru;
    mutable std::unordered_map<uint32_t, std::pair<std::wstring, std::list<uint32_t>::iterator>> m_nameCache;
    static constexpr size_t kWideNameCacheCapacity = 256;
    std::string m_pendingSearchQuery;
    QuerySortKey m_currentSortKey = QuerySortKey::Name;
    bool m_currentSortDescending = false;
    std::mutex m_searchSyncMutex;
    std::mutex m_resultBufferMutex;
    std::condition_variable m_searchEvent;
    std::atomic<bool> m_isSearchRequested;
    std::atomic<bool> m_isSortOnlyRequested;
    std::shared_ptr<std::vector<uint32_t>> m_currentResults;
    std::condition_variable m_resultCv;
};

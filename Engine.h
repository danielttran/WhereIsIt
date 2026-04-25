#pragma once

#include <vector>
#include <string>
#include <thread>
#include <atomic>
#include <stdint.h>
#include <mutex>
#include <condition_variable>
#include <memory>
#include <unordered_set>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>

enum class DriveFileSystem {
    Unknown,
    NTFS,
    Generic
};

#pragma pack(push, 1)
struct FileRecord {
    uint32_t NamePoolOffset; 
    uint32_t ParentMftIndex; 
    uint32_t MftIndex;      
    uint64_t FileSize;      
    uint64_t LastModified;  
    uint16_t FileAttributes;
    uint16_t MftSequence;   
    uint16_t ParentSequence;
    uint8_t  DriveIndex;    
};
#pragma pack(pop)

// Internal NTFS Direct-Disk Structures
#pragma pack(push, 1)
struct MFT_RECORD_HEADER {
    uint32_t Magic; uint16_t UpdateSeqOffset; uint16_t UpdateSeqSize; uint64_t LSN;
    uint16_t SequenceNumber; uint16_t HardLinkCount; uint16_t AttributeOffset;
    uint16_t Flags; uint32_t UsedSize; uint32_t AllocatedSize; uint64_t BaseRecord; uint16_t NextAttributeID;
};
struct MFT_ATTRIBUTE {
    uint32_t Type; uint32_t Length; uint8_t NonResident; uint8_t NameLength;
    uint16_t NameOffset; uint16_t Flags; uint16_t AttributeID;
};
struct MFT_RESIDENT_ATTRIBUTE { MFT_ATTRIBUTE Header; uint32_t ValueLength; uint16_t ValueOffset; uint8_t Flags; uint8_t Reserved; };
struct MFT_FILE_NAME {
    uint64_t ParentDirectory; uint64_t CreationTime; uint64_t ChangeTime; uint64_t LastWriteTime;
    uint64_t LastAccessTime; uint64_t AllocatedSize; uint64_t DataSize; uint32_t FileAttributes;
    uint32_t AlignmentOrReserved; uint8_t NameLength; uint8_t NameNamespace; wchar_t Name[1];
};
#pragma pack(pop)

class StringPool {
public:
    StringPool(size_t initialCapacity = 20 * 1024 * 1024);
    uint32_t AddString(const std::wstring& text);
    uint32_t AddString(const wchar_t* text, size_t length);
    const char* GetString(uint32_t offset) const;
    size_t GetSize() const { return m_pool.size(); }
    void Clear();
    void LoadRawData(const char* data, size_t size);
private:
    std::vector<char> m_pool;
};

class IndexingEngine {
public:
    struct IndexScopeConfig {
        bool IndexNetworkDrives = false;
        bool FollowReparsePoints = false;
        std::vector<std::wstring> IncludeRoots;
        std::vector<std::wstring> ExcludePathPatterns;
    };

    IndexingEngine();
    ~IndexingEngine();

    void Start();
    void Stop();
    
    void Search(const std::string& query);
    std::shared_ptr<std::vector<uint32_t>> GetSearchResults();
    bool HasNewResults() { return m_resultsUpdated.exchange(false); }

    const std::vector<FileRecord>& GetRecords() const { return m_records; }
    const StringPool& GetFileNamePool() const { return m_pool; }
    std::wstring GetCurrentStatus() const { return m_status; }
    bool IsBusy() const { return !m_ready; }

    std::wstring GetFullPath(uint32_t recordIndex) const;
    std::wstring GetParentPath(uint32_t recordIndex) const;

    bool SaveIndex(const std::wstring& filePath);
    bool LoadIndex(const std::wstring& filePath);
    void SetIndexScopeConfig(const IndexScopeConfig& config);
    IndexScopeConfig GetIndexScopeConfig() const;

private:
    void WorkerThread();
    void SearchThread();
    void MonitorChanges();

    bool DiscoverAllDrives();
    void PerformFullDriveScan();
    void ScanMftForDrive(uint8_t driveIndex);
    void ScanGenericDrive(uint8_t driveIndex, const std::wstring& path, uint32_t parentIdx, uint16_t parentSeq, std::unordered_set<uint64_t>& visitedDirs);

    void HandleUsnJournalRecord(USN_RECORD_V2* record, uint8_t driveIndex);
    
    uint32_t FetchVolumeSerialNumber(const std::wstring& driveLetter);
    std::wstring ResolveIndexSavePath();
    bool IsPathIncluded(const std::wstring& path) const;
    bool IsRootEnabled(const std::wstring& root) const;
    static bool WildcardMatchI(const wchar_t* pattern, const wchar_t* text);

    std::thread m_mainWorker;
    std::thread m_searchWorker;
    std::atomic<bool> m_running;
    std::atomic<bool> m_ready;
    std::wstring m_status;
    
    struct DriveContext { 
        std::wstring Letter; 
        std::string LetterUTF8;
        uint32_t SerialNumber; 
        uint64_t LastProcessedUsn; 
        HANDLE VolumeHandle; 
        DriveFileSystem Type;
    };
    std::vector<DriveContext> m_drives;
    
    std::vector<FileRecord> m_records;
    std::vector<std::vector<uint32_t>> m_mftLookupTables;
    StringPool m_pool;
    IndexScopeConfig m_scopeConfig;
    mutable std::mutex m_scopeConfigMutex;

    std::string m_pendingSearchQuery;
    std::mutex m_searchSyncMutex;
    std::mutex m_resultBufferMutex;
    std::condition_variable m_searchEvent;
    std::atomic<bool> m_isSearchRequested;
    std::atomic<bool> m_resultsUpdated;
    std::shared_ptr<std::vector<uint32_t>> m_currentResults;
};

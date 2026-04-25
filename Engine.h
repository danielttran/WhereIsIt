#pragma once

#include <vector>
#include <string>
#include <thread>
#include <atomic>
#include <stdint.h>
#include <mutex>
#include <condition_variable>
#include <memory>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>

#pragma pack(push, 1)
struct FileRecord {
    uint32_t NameOffset;
    uint32_t ParentID;
    uint32_t MFTIndex;
    uint64_t Size;
    uint64_t ModifiedTime;
    uint16_t Attributes;
    uint16_t Sequence;
    uint16_t ParentSequence; // Critical for identity verification
    uint8_t  DriveIdx;
};
#pragma pack(pop)

// Internal NTFS Structures
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
    uint64_t ParentDirectory; // 48-bit MFT index, 16-bit sequence
    uint64_t CreationTime; uint64_t ChangeTime; uint64_t LastWriteTime; uint64_t LastAccessTime;
    uint64_t AllocatedSize; uint64_t DataSize; uint32_t FileAttributes;
    uint32_t AlignmentOrReserved; uint8_t NameLength; uint8_t NameNamespace; wchar_t Name[1];
};
#pragma pack(pop)

class StringPool {
public:
    StringPool(size_t initialCapacity = 1024 * 1024);
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
    IndexingEngine();
    ~IndexingEngine();

    void Start();
    void Stop();
    void Search(const std::string& query);
    
    const std::vector<FileRecord>& GetRecords() const { return m_records; }
    std::shared_ptr<std::vector<uint32_t>> GetSearchResults() {
        std::lock_guard<std::mutex> lock(m_resultMutex);
        return m_currentResults;
    }
    
    const StringPool& GetPool() const { return m_pool; }
    bool IsBusy() const { return !m_ready; }
    std::wstring GetStatus() const { return m_status; }

    std::wstring GetFullPath(uint32_t recordIndex) const;
    std::wstring GetParentPath(uint32_t recordIndex) const;

    bool SaveIndex(const std::wstring& path);
    bool LoadIndex(const std::wstring& path);

    bool HasNewResults() { return m_resultsUpdated.exchange(false); }

private:
    void WorkerThread();
    void SearchThread();
    void MonitorChanges();
    void HandleUsnRecord(USN_RECORD_V2* usnRecord, uint8_t driveIdx);
    uint32_t GetVolumeSerialNumber(const std::wstring& drive);

    std::thread m_worker;
    std::thread m_searchWorker;
    std::atomic<bool> m_running;
    std::atomic<bool> m_ready;
    std::wstring m_status;
    
    struct DriveData { std::wstring Letter; uint32_t Serial; uint64_t LastUsn; HANDLE Handle; };
    std::vector<DriveData> m_drives;
    
    std::vector<FileRecord> m_records;
    std::vector<std::vector<uint32_t>> m_mftToRecords;
    StringPool m_pool;

    std::string m_pendingQuery;
    std::mutex m_searchMutex;
    std::mutex m_resultMutex;
    std::condition_variable m_searchCv;
    std::atomic<bool> m_searchPending;
    std::atomic<bool> m_resultsUpdated;
    
    std::shared_ptr<std::vector<uint32_t>> m_currentResults;
};

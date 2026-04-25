#pragma once

#include <vector>
#include <string>
#include <thread>
#include <atomic>
#include <stdint.h>

// Core Data Structure: Tightly packed C-style FileRecord
#pragma pack(push, 1)
struct FileRecord {
    uint32_t NameOffset; // Offset into the String Pool
    uint32_t ParentID;   // ID of the parent directory
    uint16_t Attributes; // File attributes
};
#pragma pack(pop)

// Internal NTFS Structures for MFT Parsing
#pragma pack(push, 1)
struct MFT_RECORD_HEADER {
    uint32_t Magic;           // "FILE"
    uint16_t UpdateSeqOffset;
    uint16_t UpdateSeqSize;
    uint64_t LSN;
    uint16_t SequenceNumber;
    uint16_t HardLinkCount;
    uint16_t AttributeOffset;
    uint16_t Flags;          // 0x01 = In Use, 0x02 = Directory
    uint32_t UsedSize;
    uint32_t AllocatedSize;
    uint64_t BaseRecord;
    uint16_t NextAttributeID;
};

struct MFT_ATTRIBUTE {
    uint32_t Type;           // 0x30 = $FILE_NAME
    uint32_t Length;
    uint8_t  NonResident;
    uint8_t  NameLength;
    uint16_t NameOffset;
    uint16_t Flags;
    uint16_t AttributeID;
};

struct MFT_RESIDENT_ATTRIBUTE {
    MFT_ATTRIBUTE Header;
    uint32_t ValueLength;
    uint16_t ValueOffset;
    uint8_t  Flags;
    uint8_t  Reserved;
};

struct MFT_FILE_NAME {
    uint64_t ParentDirectory; // 6 bytes MFT index, 2 bytes sequence
    uint64_t CreationTime;
    uint64_t ChangeTime;
    uint64_t LastWriteTime;
    uint64_t LastAccessTime;
    uint64_t AllocatedSize;
    uint64_t DataSize;
    uint32_t FileAttributes;
    uint32_t AlignmentOrReserved;
    uint8_t  NameLength;
    uint8_t  NameNamespace;
    wchar_t  Name[1];
};
#pragma pack(pop)

// Memory-efficient, contiguous byte array manager for UTF-8 null-terminated names
class StringPool {
public:
    StringPool(size_t initialCapacity = 1024 * 1024); // Default 1MB
    
    // Adds a UTF-16 string (from Win32) to the pool after converting to UTF-8
    uint32_t AddString(const std::wstring& text);
    uint32_t AddString(const wchar_t* text, size_t length);
    
    // Returns a pointer to the string at the given offset
    const char* GetString(uint32_t offset) const;

    size_t GetSize() const { return m_pool.size(); }

private:
    std::vector<char> m_pool;
};

// Volume Indexing Engine - Foundational Backend
class IndexingEngine {
public:
    IndexingEngine();
    ~IndexingEngine();

    void Start();
    void Stop();

    void Search(const std::string& query);
    
    const std::vector<FileRecord>& GetRecords() const { return m_records; }
    const std::vector<uint32_t>& GetSearchResults() const { return m_searchResults; }
    const StringPool& GetPool() const { return m_pool; }
    
    bool IsBusy() const { return !m_ready; }
    std::wstring GetStatus() const { return m_status; }

private:
    void WorkerThread();

    std::thread m_worker;
    std::atomic<bool> m_running;
    std::atomic<bool> m_ready;
    std::wstring m_status;
    
    std::vector<FileRecord> m_records;
    std::vector<uint32_t> m_searchResults;
    StringPool m_pool;
};

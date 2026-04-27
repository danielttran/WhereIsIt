#pragma once
#include <vector>
#include <stdint.h>
#include <windows.h>
#include "CoreTypes.h"
#include <algorithm>
#include <mutex>

class RecordPool {
public:
    static constexpr size_t kRecordsPerChunk = 512 * 1024;  // 524,288 records (16 MB)

    RecordPool(bool isShared = false);
    ~RecordPool();

    // Ensures we have at least 'count' capacity. If allocating new chunks, maps them.
    void Reserve(size_t count);
    
    // Get mutable reference to a record
    FileRecord& GetRecord(uint32_t index) {
        size_t ci = index / kRecordsPerChunk;
        if (ci >= m_chunks.size()) {
            static FileRecord dummy{};
            return dummy;
        }
        return m_chunks[ci].data[index % kRecordsPerChunk];
    }
    
    // Get const reference to a record
    const FileRecord& GetRecord(uint32_t index) const {
        size_t ci = index / kRecordsPerChunk;
        if (ci >= m_chunks.size()) {
            static FileRecord dummy{};
            return dummy;
        }
        return m_chunks[ci].data[index % kRecordsPerChunk];
    }
    
    void LoadFromVector(const std::vector<FileRecord>& records);
    
    void Clear();

    // Iterator-like method to process contiguous spans of records (useful for SaveIndex)
    template<typename Fn>
    void ForEachChunk(size_t totalRecords, Fn fn) const {
        size_t processed = 0;
        for (size_t ci = 0; ci < m_chunks.size() && processed < totalRecords; ++ci) {
            size_t inChunk = (std::min)(totalRecords - processed, kRecordsPerChunk);
            fn(m_chunks[ci].data, inChunk);
            processed += inChunk;
        }
    }

private:
    struct Chunk {
        HANDLE hMap = NULL;
        FileRecord* data = nullptr;
    };
    std::vector<Chunk> m_chunks;
    bool m_shared = false;
    mutable std::mutex m_reserveMutex;
};

#include "framework.h"
#include "RecordPool.h"
#include <stdio.h>

RecordPool::RecordPool(bool isShared) : m_shared(isShared) {}

RecordPool::~RecordPool() {
    Clear();
}

void RecordPool::Clear() {
    for (auto& c : m_chunks) {
        if (c.data) {
            if (m_shared) UnmapViewOfFile(c.data);
            else delete[] c.data;
        }
        if (c.hMap) CloseHandle(c.hMap);
    }
    m_chunks.clear();
}

void RecordPool::Reserve(size_t count) {
    size_t requiredChunks = (count + kRecordsPerChunk - 1) / kRecordsPerChunk;
    if (requiredChunks == 0) requiredChunks = 1;

    while (m_chunks.size() < requiredChunks) {
        Chunk c;
        size_t allocSize = kRecordsPerChunk * sizeof(FileRecord);
        if (m_shared) {
            wchar_t mapName[64];
            swprintf_s(mapName, L"Global\\WhereIsIt_RecordChunk_%zu", m_chunks.size());
            c.hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, (DWORD)allocSize, mapName);
            if (!c.hMap && GetLastError() == ERROR_ALREADY_EXISTS) {
                c.hMap = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, mapName);
            }
            if (c.hMap) {
                c.data = (FileRecord*)MapViewOfFile(c.hMap, FILE_MAP_ALL_ACCESS, 0, 0, 0);
            }
        } else {
            c.data = new FileRecord[kRecordsPerChunk]();
        }

        if (c.data) {
            m_chunks.push_back(c);
        } else {
            if (c.hMap) CloseHandle(c.hMap);
            break; // Failed to allocate
        }
    }
}

void RecordPool::LoadFromVector(const std::vector<FileRecord>& records) {
    Clear();
    Reserve(records.size());
    size_t remaining = records.size();
    size_t offset = 0;
    for (size_t ci = 0; ci < m_chunks.size() && remaining > 0; ++ci) {
        size_t toCopy = (std::min)(remaining, kRecordsPerChunk);
        memcpy(m_chunks[ci].data, records.data() + offset, toCopy * sizeof(FileRecord));
        offset += toCopy;
        remaining -= toCopy;
    }
}

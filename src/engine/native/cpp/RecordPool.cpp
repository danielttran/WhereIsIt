#include "framework.h"
#include "RecordPool.h"
#include "Utils.h"
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
    m_mappedCount.store(0, std::memory_order_release);
}

void RecordPool::Reserve(size_t count) {
    // Fast-path: if the requested count is already covered by mapped chunks, skip the mutex.
    if (count <= m_mappedCount.load(std::memory_order_acquire)) return;

    std::lock_guard<std::mutex> lock(m_reserveMutex);
    // Double-check under lock — another thread may have mapped while we waited.
    if (count <= m_mappedCount.load(std::memory_order_relaxed)) return;

    size_t requiredChunks = (count + kRecordsPerChunk - 1) / kRecordsPerChunk;
    if (requiredChunks == 0) requiredChunks = 1;

    while (m_chunks.size() < requiredChunks) {
        Chunk c;
        size_t allocSize = kRecordsPerChunk * sizeof(FileRecord);
        if (m_shared) {
            wchar_t mapName[128];

            // Try Global\ first
            swprintf_s(mapName, L"Global\\WhereIsIt_v10_%lu_RecordChunk_%zu", GetCurrentProcessId(), m_chunks.size());
            c.hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, GetSharedMemoryReadOnlySA(), PAGE_READWRITE, 0, (DWORD)allocSize, mapName);
            if (c.hMap) {
                c.data = (FileRecord*)MapViewOfFile(c.hMap, FILE_MAP_ALL_ACCESS, 0, 0, 0);
                if (!c.data) {
                    CloseHandle(c.hMap);
                    c.hMap = NULL;
                }
            }

            // Try Local\ if Global failed or its view wasn't writable
            if (!c.data) {
                swprintf_s(mapName, L"Local\\WhereIsIt_v10_%lu_RecordChunk_%zu", GetCurrentProcessId(), m_chunks.size());
                c.hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, (DWORD)allocSize, mapName);
                if (c.hMap) {
                    c.data = (FileRecord*)MapViewOfFile(c.hMap, FILE_MAP_ALL_ACCESS, 0, 0, 0);
                    if (!c.data) {
                        CloseHandle(c.hMap);
                        c.hMap = NULL;
                    }
                }
            }

            // Heap fallback if all mapping attempts failed
            if (!c.data) {
                Logger::Log(L"[WhereIsIt] RecordPool: All mapping attempts failed, using heap. Error: " + std::to_wstring(GetLastError()));
                c.data = new FileRecord[kRecordsPerChunk]();
            }
        } else {
            c.data = new FileRecord[kRecordsPerChunk]();
        }

        if (c.data) {
            m_chunks.push_back(c);
            m_mappedCount.store(m_chunks.size() * kRecordsPerChunk, std::memory_order_release);
        } else {
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

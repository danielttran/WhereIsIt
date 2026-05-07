#include "framework.h"
#include "StringPool.h"
#include "Utils.h"

// Reserve `needed` bytes in the current chunk; allocate a new chunk if full.
// Returns a pointer to the start of the reserved space.
char* StringPool::Reserve(size_t needed) {
    if (m_chunks.empty() || m_chunkUsed + needed > kChunkSize) {
        size_t allocSize = (needed > kChunkSize) ? needed : kChunkSize;
        size_t chunkIdx = m_chunks.size();
        wchar_t mapName[64];
        const wchar_t* pMapName = nullptr;
        if (m_shared) {
            swprintf_s(mapName, L"Global\\WhereIsIt_PoolChunk_%zu", chunkIdx);
            pMapName = mapName;
        }

        HANDLE hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, GetSharedMemoryReadOnlySA(), PAGE_READWRITE,
            (DWORD)((allocSize + 32) >> 32), (DWORD)((allocSize + 32) & 0xFFFFFFFF), pMapName);
        
        if (!hMap && pMapName && GetLastError() == ERROR_ACCESS_DENIED) {
            hMap = OpenFileMappingW(FILE_MAP_READ, FALSE, pMapName);
        }

        char* view = nullptr;
        if (hMap) {
            view = (char*)MapViewOfFile(hMap, FILE_MAP_WRITE, 0, 0, allocSize + 32);
            if (!view) view = (char*)MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0);
            
            if (!view) {
                CloseHandle(hMap);
                hMap = NULL;
            }
        }
        if (!view) {
            // Fallback (shouldn't happen in practice)
            view = new char[allocSize + 32];
        }
        m_chunks.push_back({ hMap, view });
        m_chunkUsed = 0;
    }
    char* ptr = m_chunks.back().data + m_chunkUsed;
    m_chunkUsed  += needed;
    m_totalSize  += needed;
    return ptr;
}

StringPool::StringPool(bool isShared) : m_shared(isShared) {
    // Seed chunk 0 with a single null byte so offset 0 == empty string.
    Reserve(1)[0] = '\0';
}

StringPool::~StringPool() {
    for (auto& chunk : m_chunks) {
        if (chunk.hMap) {
            UnmapViewOfFile(chunk.data);
            CloseHandle(chunk.hMap);
        } else if (chunk.data) {
            delete[] chunk.data;
        }
    }
    m_chunks.clear();
}

void StringPool::Clear() {
    for (auto& chunk : m_chunks) {
        if (chunk.hMap) {
            UnmapViewOfFile(chunk.data);
            CloseHandle(chunk.hMap);
        } else if (chunk.data) {
            delete[] chunk.data;
        }
    }
    m_chunks.clear();
    m_chunkUsed = 0;
    m_totalSize = 0;
    // Re-seed the null terminator at offset 0.
    Reserve(1)[0] = '\0';
}

// Decode a flat offset into (chunk, position) and return a pointer.
// Offsets are assigned sequentially: chunk 0 owns [0, kChunkSize), chunk 1
// owns [kChunkSize, 2*kChunkSize), etc.  Strings never straddle chunk boundaries
// because Reserve() opens a fresh chunk when there isn't enough room.
const char* StringPool::GetString(uint32_t offset) const {
    if (m_chunks.empty()) return "";
    size_t chunk = offset / kChunkSize;
    size_t pos   = offset % kChunkSize;
    if (chunk >= m_chunks.size()) return "";
    return m_chunks[chunk].data + pos;
}

void StringPool::LoadRawData(const char* data, size_t size) {
    for (auto& chunk : m_chunks) {
        if (chunk.hMap) {
            UnmapViewOfFile(chunk.data);
            CloseHandle(chunk.hMap);
        } else if (chunk.data) {
            delete[] chunk.data;
        }
    }
    m_chunks.clear();
    m_chunkUsed = 0;
    m_totalSize = 0;

    if (size == 0) {
        Reserve(1)[0] = '\0';
        return;
    }

    // Flat import: split the incoming buffer into kChunkSize pages.
    // By keeping m_chunkUsed = 0 initially, Reserve(take) correctly maps the first
    // 16MB of index.dat to chunk 0, instead of pushing it to chunk 1.
    size_t remaining = size;
    const char* src  = data;
    while (remaining > 0) {
        size_t take = (remaining > kChunkSize) ? kChunkSize : remaining;
        char* dst = Reserve(take);
        memcpy(dst, src, take);
        src       += take;
        remaining -= take;
    }
}

uint32_t StringPool::AddRawData(const char* data, size_t size) {
    if (size == 0) return 0;
    uint32_t offset = (uint32_t)m_totalSize;
    // If the data fits in the remaining space of the current chunk, a single
    // Reserve call suffices.  Otherwise it is split across chunks, which means
    // GetString(offset) would straddle a chunk boundary — so force a new chunk.
    if (!m_chunks.empty() && m_chunkUsed + size > kChunkSize) {
        // Pad current chunk to boundary so offsets stay chunk-aligned.
        size_t pad = kChunkSize - m_chunkUsed;
        if (pad > 0) { Reserve(pad); offset = (uint32_t)m_totalSize; }
    }
    char* dst = Reserve(size);
    memcpy(dst, data, size);
    return offset;
}


uint32_t StringPool::AddString(const std::wstring& text) {
    int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, NULL, 0, NULL, NULL);
    if (bytes <= 1) return 0;
    uint32_t offset = (uint32_t)m_totalSize;
    if (!m_chunks.empty() && m_chunkUsed + (size_t)bytes > kChunkSize) {
        size_t pad = kChunkSize - m_chunkUsed;
        if (pad > 0) { Reserve(pad); offset = (uint32_t)m_totalSize; }
    }
    char* dst = Reserve((size_t)bytes);
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, dst, bytes, NULL, NULL);
    return offset;
}

uint32_t StringPool::AddString(const wchar_t* text, size_t length) {
    if (!text || length == 0) return 0;
    int bytes = WideCharToMultiByte(CP_UTF8, 0, text, (int)length, NULL, 0, NULL, NULL);
    if (bytes <= 0) return 0;
    size_t total = (size_t)bytes + 1;  // +1 for null terminator
    uint32_t offset = (uint32_t)m_totalSize;
    if (!m_chunks.empty() && m_chunkUsed + total > kChunkSize) {
        size_t pad = kChunkSize - m_chunkUsed;
        if (pad > 0) { Reserve(pad); offset = (uint32_t)m_totalSize; }
    }
    char* dst = Reserve(total);
    WideCharToMultiByte(CP_UTF8, 0, text, (int)length, dst, bytes, NULL, NULL);
    dst[bytes] = '\0';
    return offset;
}

void StringPool::WriteRawTo(uint8_t* dest) const {
    size_t written = 0;
    for (size_t ci = 0; ci < m_chunks.size(); ++ci) {
        bool isLast   = (ci == m_chunks.size() - 1);
        size_t bytes  = isLast ? m_chunkUsed : kChunkSize;
        memcpy(dest + written, m_chunks[ci].data, bytes);
        written += bytes;
    }
}

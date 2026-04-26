#pragma once
#include <vector>
#include <string>
#include <stdint.h>
#include <windows.h>

class StringPool {
public:
    static constexpr size_t kChunkSize = 16 * 1024 * 1024;  // 16 MB per chunk

    StringPool(bool isShared = false);
    ~StringPool();
    uint32_t AddString(const std::wstring& text);
    uint32_t AddString(const wchar_t* text, size_t length);
    const char* GetString(uint32_t offset) const;
    size_t GetSize() const { return m_totalSize; }
    void Clear();
    void LoadRawData(const char* data, size_t size);
    uint32_t AddRawData(const char* data, size_t size);
    // Write all chunks contiguously into a pre-allocated destination buffer (used by SaveIndex).
    void WriteRawTo(uint8_t* dest) const;
    // Iterate each contiguous chunk: calls fn(data, bytes) per chunk in order.
    template<typename Fn>
    void ForEachChunk(Fn fn) const {
        for (size_t ci = 0; ci < m_chunks.size(); ++ci) {
            bool isLast  = (ci == m_chunks.size() - 1);
            size_t bytes = isLast ? m_chunkUsed : kChunkSize;
            if (bytes > 0) fn(m_chunks[ci].data, bytes);
        }
    }
private:
    struct Chunk {
        HANDLE hMap = NULL;
        char* data = nullptr;
    };
    std::vector<Chunk> m_chunks;
    size_t m_chunkUsed = 0;  // bytes used in the current (last) chunk
    size_t m_totalSize = 0;  // total bytes across all chunks
    bool m_shared = false;

    // Ensure there is room for `needed` bytes in the current chunk,
    // allocating a new chunk if necessary.
    char* Reserve(size_t needed);
};

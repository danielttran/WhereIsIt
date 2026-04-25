#include "framework.h"
#include "Engine.h"
#include <iostream>
#include <debugapi.h>
#include <winioctl.h>
#include <string>

// StringPool Implementation
StringPool::StringPool(size_t initialCapacity) {
    m_pool.reserve(initialCapacity);
    // Offset 0 is reserved for empty/null strings
    m_pool.push_back('\0');
}

uint32_t StringPool::AddString(const std::wstring& text) {
    return AddString(text.c_str(), text.size());
}

uint32_t StringPool::AddString(const wchar_t* text, size_t length) {
    if (!text || length == 0) return 0;

    // Convert UTF-16 to UTF-8
    int sizeNeeded = WideCharToMultiByte(CP_UTF8, 0, text, (int)length, NULL, 0, NULL, NULL);
    if (sizeNeeded <= 0) return 0;

    uint32_t offset = (uint32_t)m_pool.size();
    m_pool.resize(offset + sizeNeeded + 1);
    
    WideCharToMultiByte(CP_UTF8, 0, text, (int)length, &m_pool[offset], sizeNeeded, NULL, NULL);
    m_pool[offset + sizeNeeded] = '\0'; // Ensure null termination

    return offset;
}

const char* StringPool::GetString(uint32_t offset) const {
    if (offset >= m_pool.size()) return "";
    return &m_pool[offset];
}

// IndexingEngine Implementation
IndexingEngine::IndexingEngine() : m_running(false), m_ready(false), m_pool(10 * 1024 * 1024) {
    m_records.reserve(1000000); // Reserve for 1 million records
    m_searchResults.reserve(1000000);
}

IndexingEngine::~IndexingEngine() {
    Stop();
}

void IndexingEngine::Start() {
    if (m_running) return;
    
    m_running = true;
    m_worker = std::thread(&IndexingEngine::WorkerThread, this);
}

void IndexingEngine::Stop() {
    if (!m_running) return;

    m_running = false;
    if (m_worker.joinable()) {
        m_worker.join();
    }
}

void IndexingEngine::Search(const std::string& query) {
    m_searchResults.clear();
    if (query.empty()) {
        // Optional: show all records or none. Let's show all for now.
        for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) {
            m_searchResults.push_back(i);
        }
        return;
    }

    // High performance linear scan (Search Pillar)
    // In a real app, this would use multi-threading or SIMD
    for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) {
        const char* name = m_pool.GetString(m_records[i].NameOffset);
        if (strstr(name, query.c_str()) != nullptr) {
            m_searchResults.push_back(i);
        }
    }
}

void IndexingEngine::WorkerThread() {
    m_ready = false;
    m_status = L"Starting Indexer...";
    OutputDebugString(L"[Backend] Volume Indexing Engine started successfully.\n");

    // Volume Acquisition
    // Using FILE_FLAG_BACKUP_SEMANTICS for backup intent
    HANDLE hVolume = CreateFileW(L"\\\\.\\C:", 
        GENERIC_READ, 
        FILE_SHARE_READ | FILE_SHARE_WRITE, 
        NULL, 
        OPEN_EXISTING, 
        FILE_FLAG_BACKUP_SEMANTICS, 
        NULL);

    if (hVolume == INVALID_HANDLE_VALUE) {
        DWORD err = GetLastError();
        wchar_t buf[256];
        if (err == ERROR_ACCESS_DENIED)
            m_status = L"ERROR: Access Denied. Please run as Administrator.";
        else
            swprintf_s(buf, L"ERROR: Could not open C: (Error %u)", err);
        
        if (err != ERROR_ACCESS_DENIED) m_status = buf;

        swprintf_s(buf, L"[Backend] ERROR: Could not acquire volume handle (Error: %u). Ensure app is running as Administrator.\n", err);
        OutputDebugString(buf);
        m_running = false;
        m_ready = true;
        return;
    }

    m_status = L"Scanning NTFS Geometry...";
    // Geometry Extraction
    NTFS_VOLUME_DATA_BUFFER ntfsData;
    DWORD bytesReturned;
    BOOL result = DeviceIoControl(hVolume, 
        FSCTL_GET_NTFS_VOLUME_DATA, 
        NULL, 0, 
        &ntfsData, sizeof(ntfsData), 
        &bytesReturned, NULL);

    if (result) {
        wchar_t buf[256];
        swprintf_s(buf, L"[Backend] Volume Data: BytesPerCluster=%u, BytesPerFileRecordSegment=%u\n", 
            ntfsData.BytesPerCluster, ntfsData.BytesPerFileRecordSegment);
        OutputDebugString(buf);
    } else {
        DWORD err = GetLastError();
        m_status = L"ERROR: Failed to get NTFS volume data.";
        m_running = false;
        m_ready = true;
        CloseHandle(hVolume);
        return;
    }

    m_status = L"Mapping MFT physical extents...";
    // MFT Mapping
    struct MftExtent { 
        uint64_t logicalCluster; 
        uint64_t clusterCount; 
    };
    std::vector<MftExtent> mftExtents;

    // Direct MFT Mapping by reading Record 0
    std::vector<uint8_t> record0(ntfsData.BytesPerFileRecordSegment);
    LARGE_INTEGER mftStartOffset;
    mftStartOffset.QuadPart = ntfsData.MftStartLcn.QuadPart * ntfsData.BytesPerCluster;
    
    DWORD bytesRead;
    if (SetFilePointerEx(hVolume, mftStartOffset, NULL, FILE_BEGIN) && 
        ReadFile(hVolume, record0.data(), ntfsData.BytesPerFileRecordSegment, &bytesRead, NULL)) 
    {
        MFT_RECORD_HEADER* header = (MFT_RECORD_HEADER*)record0.data();
        if (header->Magic == 0x454C4946) { // "FILE"
            uint32_t attrOffset = header->AttributeOffset;
            while (attrOffset + sizeof(MFT_ATTRIBUTE) < ntfsData.BytesPerFileRecordSegment) {
                MFT_ATTRIBUTE* attr = (MFT_ATTRIBUTE*)&record0[attrOffset];
                if (attr->Type == 0xFFFFFFFF) break;

                if (attr->Type == 0x80) { // $DATA attribute
                    // MFT $DATA is always non-resident
                    struct MFT_NONRESIDENT_ATTRIBUTE {
                        MFT_ATTRIBUTE Header;
                        uint64_t StartingVcn;
                        uint64_t LastVcn;
                        uint16_t RunListOffset;
                        uint16_t CompressionUnitSize;
                        uint32_t Padding;
                        uint64_t AllocatedSize;
                        uint64_t DataSize;
                        uint64_t ValidDataSize;
                    }* nrAttr = (MFT_NONRESIDENT_ATTRIBUTE*)attr;

                    uint8_t* runList = (uint8_t*)attr + nrAttr->RunListOffset;
                    int64_t currentLcn = 0;
                    
                    // Decode NTFS Run-List
                    while (*runList != 0) {
                        uint8_t headerByte = *runList++;
                        int lenSize = headerByte & 0xF;
                        int offsetSize = headerByte >> 4;

                        uint64_t length = 0;
                        for (int i = 0; i < lenSize; i++) length |= (uint64_t)(*runList++) << (i * 8);

                        int64_t offset = 0;
                        for (int i = 0; i < offsetSize; i++) offset |= (int64_t)(*runList++) << (i * 8);
                        
                        // Sign extend the offset
                        if (offsetSize > 0 && (offset >> (offsetSize * 8 - 1)) & 1) {
                            for (int i = offsetSize; i < 8; i++) offset |= (int64_t)0xFF << (i * 8);
                        }

                        currentLcn += offset;
                        mftExtents.push_back({ (uint64_t)currentLcn, length });
                    }
                    break;
                }
                if (attr->Length == 0) break;
                attrOffset += attr->Length;
            }
        }
    }

    if (!mftExtents.empty()) {
        m_status = L"Reading MFT records (Bulk I/O)...";
        // Sequential Bulk Read & Parse
        uint64_t validRecords = 0;
        for (const auto& extent : mftExtents) {
            LARGE_INTEGER offset;
            offset.QuadPart = extent.logicalCluster * ntfsData.BytesPerCluster;

            if (!SetFilePointerEx(hVolume, offset, NULL, FILE_BEGIN)) continue;

            uint64_t bytesToRead = extent.clusterCount * ntfsData.BytesPerCluster;
            std::vector<uint8_t> extentBuffer(static_cast<size_t>(bytesToRead));
            DWORD bytesRead;
            if (ReadFile(hVolume, extentBuffer.data(), static_cast<DWORD>(bytesToRead), &bytesRead, NULL)) {
                for (size_t i = 0; i + ntfsData.BytesPerFileRecordSegment <= bytesRead; i += ntfsData.BytesPerFileRecordSegment) {
                    MFT_RECORD_HEADER* header = (MFT_RECORD_HEADER*)&extentBuffer[i];
                    
                    if (header->Magic != 0x454C4946) continue; // "FILE"
                    if (!(header->Flags & 0x01)) continue;     // Only "In Use" records

                    // Locate $FILE_NAME attribute (0x30)
                    uint32_t attrOffset = header->AttributeOffset;
                    while (attrOffset + sizeof(MFT_ATTRIBUTE) < ntfsData.BytesPerFileRecordSegment) {
                        MFT_ATTRIBUTE* attr = (MFT_ATTRIBUTE*)&extentBuffer[i + attrOffset];
                        if (attr->Type == 0xFFFFFFFF) break; // End of attributes

                        if (attr->Type == 0x30) { // $FILE_NAME
                            MFT_RESIDENT_ATTRIBUTE* resAttr = (MFT_RESIDENT_ATTRIBUTE*)attr;
                            MFT_FILE_NAME* fileName = (MFT_FILE_NAME*)&extentBuffer[i + attrOffset + resAttr->ValueOffset];
                            
                            if (fileName->NameNamespace != 2) { 
                                FileRecord rec;
                                rec.NameOffset = m_pool.AddString(fileName->Name, fileName->NameLength);
                                rec.ParentID = (uint32_t)(fileName->ParentDirectory & 0xFFFFFFFFFFFFLL);
                                rec.Attributes = (uint16_t)fileName->FileAttributes;
                                
                                m_records.push_back(rec);
                                validRecords++;
                                
                                if (validRecords % 10000 == 0) {
                                    wchar_t statusBuf[128];
                                    swprintf_s(statusBuf, L"Indexing... %llu files found", validRecords);
                                    m_status = statusBuf;
                                }
                            }
                            break; 
                        }
                        if (attr->Length == 0) break;
                        attrOffset += attr->Length;
                    }
                }
            }
            if (!m_running) break;
        }

        wchar_t recordBuf[256];
        swprintf_s(recordBuf, L"Ready - %llu files indexed", validRecords);
        m_status = recordBuf;

        m_ready = true;
    } else {
        m_status = L"ERROR: Could not open $MFT. Run as Administrator.";
        m_running = false;
        m_ready = true;
    }

    // Keep handle open or close it based on future needs. 
    // For now, we'll keep the loop running as requested.
    while (m_running) {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }

    CloseHandle(hVolume);
    OutputDebugString(L"[Backend] Volume Indexing Engine shutting down.\n");
}

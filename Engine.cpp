#include "framework.h"
#include "Engine.h"
#include <iostream>
#include <debugapi.h>
#include <string>
#include <fstream>
#include <algorithm>
#include <unordered_map>
#include <shlobj.h>

// --- CORE UTILITIES: High Speed Case-Insensitive Matching ---

static const unsigned char g_ToLowerLookup[256] = {
    0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,
    32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,
    64,97,98,99,100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115,116,117,118,119,120,121,122,91,92,93,94,95,
    96,97,98,99,100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115,116,117,118,119,120,121,122,123,124,125,126,127,
    128,129,130,131,132,133,134,135,136,137,138,139,140,141,142,143,144,145,146,147,148,149,150,151,152,153,154,155,156,157,158,159,
    160,161,162,163,164,165,166,167,168,169,170,171,172,173,174,175,176,177,178,179,180,181,182,183,184,185,186,187,188,189,190,191,
    192,193,194,195,196,197,198,199,200,201,202,203,204,205,206,207,208,209,210,211,212,213,214,215,216,217,218,219,220,221,222,223,
    224,225,226,227,228,229,230,231,232,233,234,235,236,237,238,239,240,241,242,243,244,245,246,247,248,249,250,251,252,253,254,255
};

static bool FastContainsIgnoreCase(const char* haystack, const char* needle) {
    if (!*needle) return true;
    for (; *haystack; ++haystack) {
        if (g_ToLowerLookup[(unsigned char)*haystack] == g_ToLowerLookup[(unsigned char)*needle]) {
            const char *h_ptr = haystack, *n_ptr = needle;
            while (*h_ptr && *n_ptr && g_ToLowerLookup[(unsigned char)*h_ptr] == g_ToLowerLookup[(unsigned char)*n_ptr]) { 
                h_ptr++; n_ptr++; 
                if (!*n_ptr) return true;
            }
        }
    }
    return false;
}

static int FastCompareIgnoreCase(const char* s1, const char* s2) {
    while (*s1 && (g_ToLowerLookup[(unsigned char)*s1] == g_ToLowerLookup[(unsigned char)*s2])) { 
        s1++; s2++; 
    }
    return (int)g_ToLowerLookup[(unsigned char)*s1] - (int)g_ToLowerLookup[(unsigned char)*s2];
}

// --- StringPool: Minimal RAM for Filenames ---

StringPool::StringPool(size_t initialCapacity) { 
    m_pool.reserve(initialCapacity); 
    m_pool.push_back('\0'); 
}

void StringPool::Clear() { 
    m_pool.clear(); 
    m_pool.push_back('\0'); 
}

void StringPool::LoadRawData(const char* data, size_t size) { 
    m_pool.assign(data, data + size); 
}

uint32_t StringPool::AddString(const std::wstring& text) {
    int bytesNeeded = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, NULL, 0, NULL, NULL);
    if (bytesNeeded <= 1) return 0;
    uint32_t offset = (uint32_t)m_pool.size();
    m_pool.resize(offset + bytesNeeded);
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, &m_pool[offset], bytesNeeded, NULL, NULL);
    return offset;
}

uint32_t StringPool::AddString(const wchar_t* text, size_t length) {
    if (!text || length == 0) return 0;
    int bytesNeeded = WideCharToMultiByte(CP_UTF8, 0, text, (int)length, NULL, 0, NULL, NULL);
    if (bytesNeeded <= 0) return 0;
    uint32_t offset = (uint32_t)m_pool.size();
    m_pool.resize(offset + bytesNeeded + 1);
    WideCharToMultiByte(CP_UTF8, 0, text, (int)length, &m_pool[offset], bytesNeeded, NULL, NULL);
    m_pool[offset + bytesNeeded] = '\0';
    return offset;
}

const char* StringPool::GetString(uint32_t offset) const { 
    if (offset >= (uint32_t)m_pool.size()) return ""; 
    return &m_pool[offset]; 
}

// --- IndexingEngine Implementation ---

IndexingEngine::IndexingEngine() : m_running(false), m_ready(false), m_pool(20 * 1024 * 1024), m_isSearchRequested(false), m_resultsUpdated(false) {
    m_records.reserve(1000000);
    m_currentResults = std::make_shared<std::vector<uint32_t>>();
}

IndexingEngine::~IndexingEngine() { Stop(); }

void IndexingEngine::Start() {
    if (m_running) return;
    m_running = true;
    m_mainWorker = std::thread(&IndexingEngine::WorkerThread, this);
    m_searchWorker = std::thread(&IndexingEngine::SearchThread, this);
}

void IndexingEngine::Stop() {
    m_running = false;
    m_searchEvent.notify_all();
    if (m_mainWorker.joinable()) m_mainWorker.join();
    if (m_searchWorker.joinable()) m_searchWorker.join();
}

std::shared_ptr<std::vector<uint32_t>> IndexingEngine::GetSearchResults() {
    std::lock_guard<std::mutex> lock(m_resultBufferMutex);
    return m_currentResults;
}

uint32_t IndexingEngine::FetchVolumeSerialNumber(const std::wstring& drive) {
    DWORD sn = 0; if (GetVolumeInformationW(drive.c_str(), NULL, 0, &sn, NULL, NULL, NULL, 0)) return (uint32_t)sn;
    return 0;
}

std::wstring IndexingEngine::ResolveIndexSavePath() {
    wchar_t path[MAX_PATH]; GetModuleFileNameW(NULL, path, MAX_PATH);
    std::wstring s(path); return s.substr(0, s.find_last_of(L'\\')) + L"\\index.dat";
}

bool IndexingEngine::SaveIndex(const std::wstring& filePath) {
    std::ofstream out(filePath, std::ios::binary); if (!out) return false;
    struct IndexHeader { uint32_t Magic, Version, DriveCount, RecordCount, PoolSize; } h = { 0x54494957, 5, (uint32_t)m_drives.size(), (uint32_t)m_records.size(), (uint32_t)m_pool.GetSize() };
    out.write((char*)&h, sizeof(h));
    for (const auto& d : m_drives) {
        struct DriveInfoBin { wchar_t Letter[4]; uint32_t Serial; uint64_t LastUsn; } di = { 0 }; 
        wcscpy_s(di.Letter, d.Letter.c_str()); di.Serial = d.SerialNumber; di.LastUsn = d.LastProcessedUsn;
        out.write((char*)&di, sizeof(di));
    }
    out.write((char*)m_records.data(), (std::streamsize)(m_records.size() * sizeof(FileRecord)));
    out.write(m_pool.GetString(0), (std::streamsize)m_pool.GetSize());
    for (const auto& m : m_mftLookupTables) {
        uint32_t sz = (uint32_t)m.size(); out.write((char*)&sz, sizeof(uint32_t));
        out.write((char*)m.data(), (std::streamsize)(sz * sizeof(uint32_t)));
    }
    return true;
}

bool IndexingEngine::LoadIndex(const std::wstring& filePath) {
    std::ifstream in(filePath, std::ios::binary); if (!in) return false;
    struct IndexHeader { uint32_t Magic, Version, DriveCount, RecordCount, PoolSize; } h; 
    in.read((char*)&h, sizeof(h)); if (h.Magic != 0x54494957 || h.Version != 5) return false;
    std::vector<DriveContext> tempDrives;
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        struct DriveInfoBin { wchar_t Letter[4]; uint32_t Serial; uint64_t LastUsn; } di; 
        in.read((char*)&di, sizeof(di));
        if (FetchVolumeSerialNumber(di.Letter) != di.Serial) return false;
        int utf8Len = WideCharToMultiByte(CP_UTF8, 0, di.Letter, -1, NULL, 0, NULL, NULL);
        std::string letterUTF8(utf8Len - 1, '\0');
        WideCharToMultiByte(CP_UTF8, 0, di.Letter, -1, &letterUTF8[0], utf8Len, NULL, NULL);
        tempDrives.push_back({ di.Letter, letterUTF8, di.Serial, di.LastUsn, INVALID_HANDLE_VALUE, DriveFileSystem::Unknown });
    }
    m_drives = std::move(tempDrives);
    m_records.resize(h.RecordCount); in.read((char*)m_records.data(), (std::streamsize)(h.RecordCount * sizeof(FileRecord)));
    std::vector<char> pd(h.PoolSize); in.read(pd.data(), (std::streamsize)h.PoolSize); m_pool.LoadRawData(pd.data(), h.PoolSize);
    m_mftLookupTables.resize(h.DriveCount);
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        uint32_t sz; in.read((char*)&sz, sizeof(uint32_t));
        m_mftLookupTables[i].resize(sz); in.read((char*)m_mftLookupTables[i].data(), (std::streamsize)(sz * sizeof(uint32_t)));
    }
    return true;
}

std::wstring IndexingEngine::GetFullPath(uint32_t recordIdx) const {
    if (recordIdx >= (uint32_t)m_records.size()) return L"";
    const char* parts[64]; int depth = 0; uint32_t cur = recordIdx; uint8_t di = m_records[recordIdx].DriveIndex;
    while (depth < 64) {
        const FileRecord& r = m_records[cur]; parts[depth++] = m_pool.GetString(r.NamePoolOffset);
        if (r.ParentMftIndex < 5 || r.ParentMftIndex >= (uint32_t)m_mftLookupTables[di].size()) break;
        uint32_t pi = m_mftLookupTables[di][r.ParentMftIndex];
        if (pi == 0xFFFFFFFF || m_records[pi].MftSequence != r.ParentSequence) break;
        if (pi == cur) break; cur = pi;
    }
    std::string pa; for (int i = depth - 1; i >= 0; --i) { if (!pa.empty()) pa += "\\"; pa += parts[i]; }
    int sz = MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, NULL, 0);
    if (sz > 0) { std::wstring pw(sz - 1, L'\0'); MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, &pw[0], sz); return m_drives[di].Letter + pw; }
    return m_drives[di].Letter;
}

std::wstring IndexingEngine::GetParentPath(uint32_t recordIdx) const {
    if (recordIdx >= (uint32_t)m_records.size()) return L"";
    const FileRecord& child = m_records[recordIdx]; uint8_t di = child.DriveIndex;
    if (child.ParentMftIndex < 5 || child.ParentMftIndex >= (uint32_t)m_mftLookupTables[di].size()) return m_drives[di].Letter;
    uint32_t pi = m_mftLookupTables[di][child.ParentMftIndex];
    if (pi == 0xFFFFFFFF || m_records[pi].MftSequence != child.ParentSequence) return m_drives[di].Letter;
    return GetFullPath(pi);
}

void IndexingEngine::Search(const std::string& q) {
    { std::lock_guard<std::mutex> lock(m_searchSyncMutex); m_pendingSearchQuery = q; m_isSearchRequested = true; }
    m_searchEvent.notify_one();
}

void IndexingEngine::SearchThread() {
    std::string query;
    while (m_running) {
        {
            std::unique_lock<std::mutex> lock(m_searchSyncMutex);
            m_searchEvent.wait(lock, [this] { return !m_running || m_isSearchRequested; });
            if (!m_running) break;
            query = m_pendingSearchQuery; m_isSearchRequested = false;
        }

        auto results = std::make_shared<std::vector<uint32_t>>();
        const char* qCStr = query.c_str();

        if (query.empty()) {
            results->reserve(m_records.size());
            for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) if (m_records[i].MftIndex != 0xFFFFFFFF) results->push_back(i);
            { std::lock_guard<std::mutex> lock(m_resultBufferMutex); m_currentResults = results; m_resultsUpdated = true; }
            auto sortedResults = std::make_shared<std::vector<uint32_t>>(*results);
            std::sort(sortedResults->begin(), sortedResults->end(), [this](uint32_t a, uint32_t b) {
                return FastCompareIgnoreCase(m_pool.GetString(m_records[a].NamePoolOffset), m_pool.GetString(m_records[b].NamePoolOffset)) < 0;
            });
            results = std::move(sortedResults);
        } else {
            bool hasSep = (query.find('\\') != std::string::npos);
            for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) {
                const auto& rec = m_records[i]; if (rec.MftIndex == 0xFFFFFFFF) continue;
                const char* name = m_pool.GetString(rec.NamePoolOffset);
                
                // 1. Match Filename
                if (FastContainsIgnoreCase(name, qCStr)) {
                    results->push_back(i);
                } 
                // 2. Match Path (Only if backslash present, to avoid over-matching drive letters)
                else if (hasSep) {
                    uint32_t curParentMft = rec.ParentMftIndex;
                    uint16_t curParentSeq = rec.ParentSequence;
                    uint8_t dIdx = rec.DriveIndex;
                    bool pathMatched = false;

                    // Match Drive Letter
                    if (FastContainsIgnoreCase(m_drives[dIdx].LetterUTF8.c_str(), qCStr)) pathMatched = true;
                    
                    // Match Folders (Jump up the tree - No string building required)
                    while (!pathMatched && curParentMft >= 5 && curParentMft < (uint32_t)m_mftLookupTables[dIdx].size()) {
                        uint32_t pIdx = m_mftLookupTables[dIdx][curParentMft];
                        if (pIdx == 0xFFFFFFFF) break;
                        const auto& parentRec = m_records[pIdx];
                        if (parentRec.MftSequence != curParentSeq) break;
                        
                        if (FastContainsIgnoreCase(m_pool.GetString(parentRec.NamePoolOffset), qCStr)) {
                            pathMatched = true; break;
                        }
                        curParentMft = parentRec.ParentMftIndex;
                        curParentSeq = parentRec.ParentSequence;
                        if (curParentMft == parentRec.MftIndex) break; 
                    }
                    if (pathMatched) results->push_back(i);
                }
                if (m_isSearchRequested) break;
            }
        }
        
        if (!m_isSearchRequested) {
            std::lock_guard<std::mutex> lock(m_resultBufferMutex);
            m_currentResults = std::move(results); m_resultsUpdated = true;
        }
    }
}

void IndexingEngine::HandleUsnJournalRecord(USN_RECORD_V2* r, uint8_t di) {
    uint32_t mftIdx = (uint32_t)(r->FileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t seq = (uint16_t)(r->FileReferenceNumber >> 48);
    uint32_t pIdx = (uint32_t)(r->ParentFileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t pSeq = (uint16_t)(r->ParentFileReferenceNumber >> 48);
    if (r->Reason & USN_REASON_FILE_DELETE) {
        if (mftIdx < (uint32_t)m_mftLookupTables[di].size()) {
            uint32_t idx = m_mftLookupTables[di][mftIdx];
            if (idx != 0xFFFFFFFF && m_records[idx].MftSequence == seq) { 
                m_records[idx].MftIndex = 0xFFFFFFFF; m_mftLookupTables[di][mftIdx] = 0xFFFFFFFF; 
            }
        }
    }
    if (r->Reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) {
        std::wstring name(r->FileName, r->FileNameLength/2);
        FileRecord rec = { m_pool.AddString(name), pIdx, mftIdx, 0, 0, (uint16_t)r->FileAttributes, seq, pSeq, di };
        std::wstring path = m_drives[di].Letter + name; WIN32_FILE_ATTRIBUTE_DATA att;
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &att)) {
            rec.FileSize = ((uint64_t)att.nFileSizeHigh << 32) | att.nFileSizeLow;
            rec.LastModified = ((uint64_t)att.ftLastWriteTime.dwHighDateTime << 32) | att.ftLastWriteTime.dwLowDateTime;
        }
        uint32_t existing = (mftIdx < (uint32_t)m_mftLookupTables[di].size()) ? m_mftLookupTables[di][mftIdx] : 0xFFFFFFFF;
        if (existing != 0xFFFFFFFF) m_records[existing] = rec;
        else {
            uint32_t idx = (uint32_t)m_records.size(); m_records.push_back(rec);
            if (mftIdx >= (uint32_t)m_mftLookupTables[di].size()) m_mftLookupTables[di].resize(mftIdx + 10000, 0xFFFFFFFF);
            m_mftLookupTables[di][mftIdx] = idx;
        }
    }
}

void IndexingEngine::MonitorChanges() {
    auto buf = std::make_unique<uint8_t[]>(65536);
    while (m_running) {
        for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
            auto& d = m_drives[i]; if (d.VolumeHandle == INVALID_HANDLE_VALUE || d.Type != DriveFileSystem::NTFS) continue;
            USN_JOURNAL_DATA_V0 uj; DWORD cb; if (!DeviceIoControl(d.VolumeHandle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) continue;
            READ_USN_JOURNAL_DATA_V0 rd = { (USN)d.LastProcessedUsn, 0xFFFFFFFF, FALSE, 0, 0, uj.UsnJournalID };
            if (DeviceIoControl(d.VolumeHandle, FSCTL_READ_USN_JOURNAL, &rd, sizeof(rd), buf.get(), 65536, &cb, NULL)) {
                if (cb > sizeof(USN)) {
                    uint8_t* p = buf.get() + sizeof(USN);
                    while (p < buf.get() + cb) { USN_RECORD_V2* record = (USN_RECORD_V2*)p; HandleUsnJournalRecord(record, i); p += record->RecordLength; }
                    d.LastProcessedUsn = (uint64_t)(*(USN*)buf.get());
                }
            } else if (GetLastError() == ERROR_JOURNAL_ENTRY_DELETED) {
                m_status = L"Journal Truncated. Sync lost.";
            }
        }
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
}

bool IndexingEngine::DiscoverAllDrives() {
    m_status = L"Discovering drives...";
    wchar_t drvs[512]; GetLogicalDriveStringsW(512, drvs);
    for (wchar_t* p = drvs; *p; p += wcslen(p) + 1) {
        UINT driveType = GetDriveTypeW(p);
        if (driveType != DRIVE_FIXED && driveType != DRIVE_REMOVABLE && driveType != DRIVE_CDROM) continue;
        wchar_t fs[32] = { 0 };
        DriveFileSystem type = DriveFileSystem::Generic;
        if (GetVolumeInformationW(p, NULL, 0, NULL, NULL, NULL, fs, 32)) {
            if (_wcsicmp(fs, L"NTFS") == 0) type = DriveFileSystem::NTFS;
        }
        std::wstring vp = L"\\\\.\\" + std::wstring(p, 2);
        HANDLE h = CreateFileW(vp.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
        if (h == INVALID_HANDLE_VALUE && type == DriveFileSystem::NTFS) {
            type = DriveFileSystem::Generic; // Can't read MFT directly; fall back to directory scan
        }
        if (h != INVALID_HANDLE_VALUE || type == DriveFileSystem::Generic) {
            int utf8Len = WideCharToMultiByte(CP_UTF8, 0, p, -1, NULL, 0, NULL, NULL);
            std::string letterUTF8(utf8Len - 1, '\0');
            WideCharToMultiByte(CP_UTF8, 0, p, -1, &letterUTF8[0], utf8Len, NULL, NULL);
            m_drives.push_back({ p, letterUTF8, FetchVolumeSerialNumber(p), 0, h, type });
        }
    }
    return !m_drives.empty();
}

void IndexingEngine::ScanGenericDrive(uint8_t di, const std::wstring& path, uint32_t pIdx, uint16_t pSeq) {
    WIN32_FIND_DATAW fd; std::wstring searchPath = path + L"*";
    HANDLE h = FindFirstFileExW(searchPath.c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
        FileRecord rec = { m_pool.AddString(fd.cFileName), pIdx, 0, ((uint64_t)fd.nFileSizeHigh << 32) | fd.nFileSizeLow, 
                          ((uint64_t)fd.ftLastWriteTime.dwHighDateTime << 32) | fd.ftLastWriteTime.dwLowDateTime, 
                          (uint16_t)fd.dwFileAttributes, 0, pSeq, di };
        uint32_t myIdx = (uint32_t)m_records.size(); m_records.push_back(rec);
        if (myIdx % 10000 == 0) m_status = L"Scanning " + m_drives[di].Letter + L"... " + std::to_wstring(m_records.size()) + L" items";
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ScanGenericDrive(di, path + fd.cFileName + L"\\", myIdx, 0);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}

void IndexingEngine::ScanMftForDrive(uint8_t di) {
    auto& d = m_drives[di]; NTFS_VOLUME_DATA_BUFFER nt; DWORD cb; DeviceIoControl(d.VolumeHandle, FSCTL_GET_NTFS_VOLUME_DATA, NULL, 0, &nt, sizeof(nt), &cb, NULL);
    USN_JOURNAL_DATA_V0 uj; if (DeviceIoControl(d.VolumeHandle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) d.LastProcessedUsn = uj.NextUsn;
    uint64_t maxMft = nt.MftValidDataLength.QuadPart / nt.BytesPerFileRecordSegment;
    m_mftLookupTables.push_back(std::vector<uint32_t>((size_t)maxMft + 100000, 0xFFFFFFFF));
    std::vector<uint8_t> r0(nt.BytesPerFileRecordSegment); LARGE_INTEGER offs; offs.QuadPart = nt.MftStartLcn.QuadPart * nt.BytesPerCluster;
    if (!SetFilePointerEx(d.VolumeHandle, offs, NULL, FILE_BEGIN) || !ReadFile(d.VolumeHandle, r0.data(), nt.BytesPerFileRecordSegment, &cb, NULL)) return;
    MFT_RECORD_HEADER* h0 = (MFT_RECORD_HEADER*)r0.data(); uint32_t ao0 = h0->AttributeOffset;
    while (ao0 + sizeof(MFT_ATTRIBUTE) < nt.BytesPerFileRecordSegment) {
        MFT_ATTRIBUTE* a = (MFT_ATTRIBUTE*)&r0[ao0];
        if (a->Type == 0x80) {
            struct MFT_NR { MFT_ATTRIBUTE h; uint64_t startVcn, lastVcn; uint16_t runOffset; }* nr = (MFT_NR*)a;
            uint8_t* rl = (uint8_t*)a + nr->runOffset; int64_t curLcn = 0; uint32_t mftCounter = 0;
            std::vector<uint8_t> eb(1024*1024); // Allocated once, reused across all data runs
            while (*rl) {
                uint8_t hb = *rl++; int ls = hb & 0xF, os = hb >> 4;
                uint64_t len = 0; for (int j=0; j<ls; j++) len |= (uint64_t)(*rl++) << (j*8);
                int64_t runOff = 0; for (int j=0; j<os; j++) runOff |= (int64_t)(*rl++) << (j*8);
                if (os > 0 && (runOff >> (os*8-1))&1) for (int j=os; j<8; j++) runOff |= (int64_t)0xFF << (j*8);
                curLcn += runOff; LARGE_INTEGER eo; eo.QuadPart = curLcn * nt.BytesPerCluster;
                SetFilePointerEx(d.VolumeHandle, eo, NULL, FILE_BEGIN); uint64_t bt = len * nt.BytesPerCluster;
                DWORD br;
                for (uint64_t r=0; r<bt; r+=br) {
                    if (!ReadFile(d.VolumeHandle, eb.data(), (DWORD)min(bt-r, 1024*1024ULL), &br, NULL) || !br) break;
                    for (uint32_t k=0; k+nt.BytesPerFileRecordSegment <= br; k+=nt.BytesPerFileRecordSegment) {
                        uint32_t tm = mftCounter++; MFT_RECORD_HEADER* rh = (MFT_RECORD_HEADER*)&eb[k];
                        if (rh->Magic != 0x454C4946 || !(rh->Flags & 0x01)) continue;
                        // Apply NTFS Update Sequence Array fixup to restore the correct bytes at
                        // each 512-byte sector boundary (corrupted on-disk to detect torn writes).
                        if (rh->UpdateSeqOffset + rh->UpdateSeqSize * (uint32_t)sizeof(uint16_t) <= nt.BytesPerFileRecordSegment) {
                            uint16_t* usa = (uint16_t*)((uint8_t*)rh + rh->UpdateSeqOffset);
                            for (uint16_t s = 1; s < rh->UpdateSeqSize; s++) {
                                uint16_t* sectorEnd = (uint16_t*)((uint8_t*)rh + s * 512 - 2);
                                if (*sectorEnd == usa[0]) *sectorEnd = usa[s];
                            }
                        }
                        uint32_t ao = rh->AttributeOffset;
                        while (ao + sizeof(MFT_ATTRIBUTE) < nt.BytesPerFileRecordSegment) {
                            MFT_ATTRIBUTE* aa = (MFT_ATTRIBUTE*)&eb[k+ao];
                            if (aa->Type == 0xFFFFFFFF) break; // End-of-attribute-list marker
                            if (aa->Type == 0x30 && !aa->NonResident) {
                                MFT_FILE_NAME* fn = (MFT_FILE_NAME*)&eb[k+ao+((MFT_RESIDENT_ATTRIBUTE*)aa)->ValueOffset];
                                if (fn->NameNamespace != 2) {
                                    FileRecord entry = { m_pool.AddString(fn->Name, fn->NameLength), (uint32_t)(fn->ParentDirectory & 0xFFFFFFFFFFFFLL), tm, fn->DataSize, fn->LastWriteTime, (uint16_t)fn->FileAttributes, rh->SequenceNumber, (uint16_t)(fn->ParentDirectory >> 48), di };
                                    if (rh->Flags & 0x02) entry.FileAttributes |= 0x10;
                                    uint32_t ri = (uint32_t)m_records.size(); m_records.push_back(entry);
                                    if (tm < (uint32_t)m_mftLookupTables[di].size()) m_mftLookupTables[di][tm] = ri;
                                    if (ri % 10000 == 0) m_status = L"Indexing " + d.Letter + L"... " + std::to_wstring(m_records.size()) + L" items";
                                }
                            }
                            if (!aa->Length) break; ao += aa->Length;
                        }
                    }
                }
            }
            break;
        }
        if (!a->Length) break; ao0 += a->Length;
    }
}

void IndexingEngine::PerformFullDriveScan() {
    m_records.clear(); m_pool.Clear(); m_mftLookupTables.clear();
    for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
        if (m_drives[i].Type == DriveFileSystem::NTFS) ScanMftForDrive(i);
        else ScanGenericDrive(i, m_drives[i].Letter, 0xFFFFFFFF, 0);
    }
}

void IndexingEngine::WorkerThread() {
    m_ready = false;
    if (!DiscoverAllDrives()) { m_ready = true; return; }
    if (LoadIndex(ResolveIndexSavePath())) {
        // Drive type is not persisted in the index; re-detect it so change monitoring works.
        for (auto& d : m_drives) {
            wchar_t fs[32] = { 0 };
            if (GetVolumeInformationW(d.Letter.c_str(), NULL, 0, NULL, NULL, NULL, fs, 32))
                d.Type = (_wcsicmp(fs, L"NTFS") == 0) ? DriveFileSystem::NTFS : DriveFileSystem::Generic;
            if (d.VolumeHandle == INVALID_HANDLE_VALUE && d.Type == DriveFileSystem::NTFS) {
                std::wstring vp = L"\\\\.\\" + d.Letter.substr(0, 2);
                d.VolumeHandle = CreateFileW(vp.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
            }
        }
    } else {
        PerformFullDriveScan();
        SaveIndex(ResolveIndexSavePath());
    }
    m_status = L"Ready - " + std::to_wstring(m_records.size()) + L" items";
    m_ready = true; Search(""); MonitorChanges();
}

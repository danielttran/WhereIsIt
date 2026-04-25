#include "framework.h"
#include "Engine.h"
#include <iostream>
#include <debugapi.h>
#include <string>
#include <fstream>
#include <algorithm>
#include <unordered_map>

// Fast ASCII tolower table
static const unsigned char g_ToLowerTable[256] = {
    0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,
    32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,
    64,97,98,99,100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115,116,117,118,119,120,121,122,91,92,93,94,95,
    96,97,98,99,100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115,116,117,118,119,120,121,122,123,124,125,126,127,
    128,129,130,131,132,133,134,135,136,137,138,139,140,141,142,143,144,145,146,147,148,149,150,151,152,153,154,155,156,157,158,159,
    160,161,162,163,164,165,166,167,168,169,170,171,172,173,174,175,176,177,178,179,180,181,182,183,184,185,186,187,188,189,190,191,
    192,193,194,195,196,197,198,199,200,201,202,203,204,205,206,207,208,209,210,211,212,213,214,215,216,217,218,219,220,221,222,223,
    224,225,226,227,228,229,230,231,232,233,234,235,236,237,238,239,240,241,242,243,244,245,246,247,248,249,250,251,252,253,254,255
};

static bool FastContainsIgnoreCase(const char* h, const char* n) {
    if (!*n) return true;
    for (; *h; ++h) {
        if (g_ToLowerTable[(unsigned char)*h] == g_ToLowerTable[(unsigned char)*n]) {
            const char *h1 = h, *n1 = n;
            while (*h1 && *n1 && g_ToLowerTable[(unsigned char)*h1] == g_ToLowerTable[(unsigned char)*n1]) { h1++; n1++; }
            if (!*n1) return true;
        }
    }
    return false;
}

static int FastCompareIgnoreCase(const char* s1, const char* s2) {
    while (*s1 && (g_ToLowerTable[(unsigned char)*s1] == g_ToLowerTable[(unsigned char)*s2])) { s1++; s2++; }
    return g_ToLowerTable[(unsigned char)*s1] - g_ToLowerTable[(unsigned char)*s2];
}

StringPool::StringPool(size_t cap) { m_pool.reserve(cap); m_pool.push_back('\0'); }
void StringPool::Clear() { m_pool.clear(); m_pool.push_back('\0'); }
void StringPool::LoadRawData(const char* d, size_t s) { m_pool.assign(d, d + s); }
uint32_t StringPool::AddString(const std::wstring& t) { return AddString(t.c_str(), t.size()); }
uint32_t StringPool::AddString(const wchar_t* t, size_t l) {
    if (!t || l == 0) return 0;
    int sz = WideCharToMultiByte(CP_UTF8, 0, t, (int)l, NULL, 0, NULL, NULL);
    if (sz <= 0) return 0;
    uint32_t off = (uint32_t)m_pool.size(); m_pool.resize(off + sz + 1);
    WideCharToMultiByte(CP_UTF8, 0, t, (int)l, &m_pool[off], sz, NULL, NULL);
    m_pool[off + sz] = '\0'; return off;
}
const char* StringPool::GetString(uint32_t off) const { if (off >= m_pool.size()) return ""; return &m_pool[off]; }

IndexingEngine::IndexingEngine() : m_running(false), m_ready(false), m_pool(10 * 1024 * 1024), m_searchPending(false), m_resultsUpdated(false) {
    m_records.reserve(1000000); m_currentResults = std::make_shared<std::vector<uint32_t>>();
}
IndexingEngine::~IndexingEngine() { Stop(); }

void IndexingEngine::Start() {
    if (m_running) return;
    m_running = true;
    m_worker = std::thread(&IndexingEngine::WorkerThread, this);
    m_searchWorker = std::thread(&IndexingEngine::SearchThread, this);
}

void IndexingEngine::Stop() {
    m_running = false; m_searchCv.notify_all();
    if (m_worker.joinable()) m_worker.join();
    if (m_searchWorker.joinable()) m_searchWorker.join();
}

uint32_t IndexingEngine::GetVolumeSerialNumber(const std::wstring& drv) {
    DWORD sn = 0; if (GetVolumeInformationW(drv.c_str(), NULL, 0, &sn, NULL, NULL, NULL, 0)) return sn;
    return 0;
}

bool IndexingEngine::SaveIndex(const std::wstring& path) {
    std::ofstream out(path, std::ios::binary); if (!out) return false;
    struct IndexHeader { uint32_t Magic, Version, DriveCount, RecordCount, PoolSize; } h = { 0x54494957, 5, (uint32_t)m_drives.size(), (uint32_t)m_records.size(), (uint32_t)m_pool.GetSize() };
    out.write((char*)&h, sizeof(h));
    for (const auto& d : m_drives) {
        struct DriveInfoBin { wchar_t Letter[4]; uint32_t Serial; uint64_t LastUsn; } di = { 0 }; wcscpy_s(di.Letter, d.Letter.c_str()); di.Serial = d.Serial; di.LastUsn = d.LastUsn;
        out.write((char*)&di, sizeof(di));
    }
    out.write((char*)m_records.data(), m_records.size() * sizeof(FileRecord));
    out.write(m_pool.GetString(0), m_pool.GetSize());
    for (const auto& m : m_mftToRecords) {
        uint32_t sz = (uint32_t)m.size(); out.write((char*)&sz, sizeof(uint32_t));
        out.write((char*)m.data(), sz * sizeof(uint32_t));
    }
    return true;
}

bool IndexingEngine::LoadIndex(const std::wstring& path) {
    std::ifstream in(path, std::ios::binary); if (!in) return false;
    struct IndexHeader { uint32_t Magic, Version, DriveCount, RecordCount, PoolSize; } h; in.read((char*)&h, sizeof(h)); 
    if (h.Magic != 0x54494957 || h.Version != 5) return false;
    m_drives.clear();
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        struct DriveInfoBin { wchar_t Letter[4]; uint32_t Serial; uint64_t LastUsn; } di; in.read((char*)&di, sizeof(di));
        if (GetVolumeSerialNumber(di.Letter) != di.Serial) return false;
        m_drives.push_back({ di.Letter, di.Serial, di.LastUsn, INVALID_HANDLE_VALUE });
    }
    m_records.resize(h.RecordCount); in.read((char*)m_records.data(), h.RecordCount * sizeof(FileRecord));
    std::vector<char> pd(h.PoolSize); in.read(pd.data(), h.PoolSize); m_pool.LoadRawData(pd.data(), h.PoolSize);
    m_mftToRecords.resize(h.DriveCount);
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        uint32_t sz; in.read((char*)&sz, sizeof(uint32_t));
        m_mftToRecords[i].resize(sz); in.read((char*)m_mftToRecords[i].data(), sz * sizeof(uint32_t));
    }
    return true;
}

std::wstring IndexingEngine::GetFullPath(uint32_t recordIdx) const {
    if (recordIdx >= m_records.size()) return L"";
    const char* parts[64]; int count = 0; uint32_t cur = recordIdx; uint8_t di = m_records[recordIdx].DriveIdx;
    while (count < 64) {
        const FileRecord& r = m_records[cur]; parts[count++] = m_pool.GetString(r.NameOffset);
        if (r.ParentID < 5 || r.ParentID >= m_mftToRecords[di].size()) break;
        uint32_t pi = m_mftToRecords[di][r.ParentID];
        if (pi == 0xFFFFFFFF || m_records[pi].Sequence != r.ParentSequence) break; // IDENTITY VERIFICATION
        if (pi == cur) break; cur = pi;
    }
    std::string pa; for (int i = count - 1; i >= 0; --i) { if (!pa.empty()) pa += "\\"; pa += parts[i]; }
    int sz = MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, NULL, 0);
    if (sz > 0) { std::wstring pw(sz - 1, L'\0'); MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, &pw[0], sz); return m_drives[di].Letter + pw; }
    return m_drives[di].Letter;
}

std::wstring IndexingEngine::GetParentPath(uint32_t recordIdx) const {
    if (recordIdx >= m_records.size()) return L"";
    const FileRecord& r = m_records[recordIdx]; uint8_t di = r.DriveIdx;
    if (r.ParentID < 5 || r.ParentID >= m_mftToRecords[di].size()) return m_drives[di].Letter;
    uint32_t pi = m_mftToRecords[di][r.ParentID];
    if (pi == 0xFFFFFFFF || m_records[pi].Sequence != r.ParentSequence) return m_drives[di].Letter;
    return GetFullPath(pi);
}

void IndexingEngine::Search(const std::string& q) {
    { std::lock_guard<std::mutex> lock(m_searchMutex); m_pendingQuery = q; m_searchPending = true; }
    m_searchCv.notify_one();
}

void IndexingEngine::SearchThread() {
    std::string q;
    while (m_running) {
        {
            std::unique_lock<std::mutex> lock(m_searchMutex);
            m_searchCv.wait(lock, [this] { return !m_running || m_searchPending; });
            if (!m_running) break;
            q = m_pendingQuery; m_searchPending = false;
        }
        auto res = std::make_shared<std::vector<uint32_t>>();
        if (q.empty()) {
            res->reserve(m_records.size());
            for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) if (m_records[i].MFTIndex != 0xFFFFFFFF) res->push_back(i);
            std::sort(res->begin(), res->end(), [this](uint32_t a, uint32_t b) {
                return FastCompareIgnoreCase(m_pool.GetString(m_records[a].NameOffset), m_pool.GetString(m_records[b].NameOffset)) < 0;
            });
        } else {
            bool hasSep = (q.find('\\') != std::string::npos || q.find('/') != std::string::npos);
            uint32_t lp = 0xFFFFFFFF; std::string lpp;
            for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) {
                const auto& rec = m_records[i]; if (rec.MFTIndex == 0xFFFFFFFF) continue;
                const char* name = m_pool.GetString(rec.NameOffset);
                if (FastContainsIgnoreCase(name, q.c_str())) { res->push_back(i); continue; }
                if (hasSep) {
                    if (rec.ParentID != lp) {
                        std::wstring fp = GetParentPath(i); char fpa[MAX_PATH * 2];
                        WideCharToMultiByte(CP_UTF8, 0, fp.c_str(), -1, fpa, sizeof(fpa), NULL, NULL);
                        lpp = fpa; lp = rec.ParentID;
                    }
                    std::string full = lpp + "\\" + name;
                    if (FastContainsIgnoreCase(full.c_str(), q.c_str())) res->push_back(i);
                }
                if (m_searchPending) break;
            }
        }
        if (!m_searchPending) {
            std::lock_guard<std::mutex> lock(m_resultMutex);
            m_currentResults = std::move(res); m_resultsUpdated = true;
        }
    }
}

void IndexingEngine::HandleUsnRecord(USN_RECORD_V2* r, uint8_t di) {
    uint32_t mftIdx = (uint32_t)(r->FileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t seq = (uint16_t)(r->FileReferenceNumber >> 48);
    uint32_t pIdx = (uint32_t)(r->ParentFileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t pSeq = (uint16_t)(r->ParentFileReferenceNumber >> 48);
    
    if (r->Reason & USN_REASON_FILE_DELETE) {
        if (mftIdx < (uint32_t)m_mftToRecords[di].size()) {
            uint32_t idx = m_mftToRecords[di][mftIdx];
            if (idx != 0xFFFFFFFF && m_records[idx].Sequence == seq) { 
                m_records[idx].MFTIndex = 0xFFFFFFFF; m_mftToRecords[di][mftIdx] = 0xFFFFFFFF; 
            }
        }
    }
    if (r->Reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) {
        std::wstring name(r->FileName, r->FileNameLength/2);
        FileRecord rec = { m_pool.AddString(name), pIdx, mftIdx, 0, 0, (uint16_t)r->FileAttributes, seq, pSeq, di };
        std::wstring path = m_drives[di].Letter + name; WIN32_FILE_ATTRIBUTE_DATA att;
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &att)) {
            rec.Size = ((uint64_t)att.nFileSizeHigh << 32) | att.nFileSizeLow;
            rec.ModifiedTime = ((uint64_t)att.ftLastWriteTime.dwHighDateTime << 32) | att.ftLastWriteTime.dwLowDateTime;
        }
        uint32_t existing = (mftIdx < m_mftToRecords[di].size()) ? m_mftToRecords[di][mftIdx] : 0xFFFFFFFF;
        if (existing != 0xFFFFFFFF) m_records[existing] = rec;
        else {
            uint32_t idx = (uint32_t)m_records.size(); m_records.push_back(rec);
            if (mftIdx >= m_mftToRecords[di].size()) m_mftToRecords[di].resize(mftIdx + 10000, 0xFFFFFFFF);
            m_mftToRecords[di][mftIdx] = idx;
        }
    }
}

void IndexingEngine::MonitorChanges() {
    uint64_t lastSave = GetTickCount64();
    while (m_running) {
        for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
            auto& d = m_drives[i]; if (d.Handle == INVALID_HANDLE_VALUE) continue;
            USN_JOURNAL_DATA_V0 uj; DWORD cb;
            if (!DeviceIoControl(d.Handle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) continue;
            READ_USN_JOURNAL_DATA_V0 rd = { d.LastUsn, 0xFFFFFFFF, FALSE, 0, 0, uj.UsnJournalID };
            uint8_t buf[65536];
            if (DeviceIoControl(d.Handle, FSCTL_READ_USN_JOURNAL, &rd, sizeof(rd), buf, sizeof(buf), &cb, NULL)) {
                if (cb > sizeof(USN)) {
                    uint8_t* p = buf + sizeof(USN);
                    while (p < buf + cb) { USN_RECORD_V2* r = (USN_RECORD_V2*)p; HandleUsnRecord(r, i); p += r->RecordLength; }
                    d.LastUsn = *(USN*)buf;
                }
            } else if (GetLastError() == ERROR_JOURNAL_ENTRY_DELETED) {
                m_status = L"Sync Error: Journal truncated. Restart app.";
            }
        }
        if (GetTickCount64() - lastSave > 300000) { SaveIndex(L"index.dat"); lastSave = GetTickCount64(); }
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
}

void IndexingEngine::WorkerThread() {
    m_ready = false; m_status = L"Discovering drives...";
    wchar_t drvs[512]; GetLogicalDriveStringsW(512, drvs);
    for (wchar_t* p = drvs; *p; p += wcslen(p) + 1) {
        if (GetDriveTypeW(p) != DRIVE_FIXED) continue;
        wchar_t fs[32]; if (GetVolumeInformationW(p, NULL, 0, NULL, NULL, NULL, fs, 32) && _wcsicmp(fs, L"NTFS") == 0) {
            std::wstring vp = L"\\\\.\\" + std::wstring(p, 2);
            HANDLE h = CreateFileW(vp.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
            if (h != INVALID_HANDLE_VALUE) m_drives.push_back({ p, GetVolumeSerialNumber(p), 0, h });
        }
    }
    if (m_drives.empty()) { m_status = L"ERROR: No NTFS drives found."; m_ready = true; return; }

    if (LoadIndex(L"index.dat")) {
        for (auto& d : m_drives) if (d.Handle == INVALID_HANDLE_VALUE) {
            std::wstring vp = L"\\\\.\\" + d.Letter.substr(0, 2);
            d.Handle = CreateFileW(vp.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
        }
    } else {
        m_records.clear(); m_pool.Clear(); m_mftToRecords.clear();
        std::unordered_map<std::wstring, uint32_t> sMap; sMap.reserve(500000);
        uint64_t total = 0;
        for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
            auto& d = m_drives[i]; NTFS_VOLUME_DATA_BUFFER nt; DWORD cb; DeviceIoControl(d.Handle, FSCTL_GET_NTFS_VOLUME_DATA, NULL, 0, &nt, sizeof(nt), &cb, NULL);
            USN_JOURNAL_DATA_V0 uj; if (DeviceIoControl(d.Handle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) d.LastUsn = uj.NextUsn;
            uint64_t maxMft = nt.MftValidDataLength.QuadPart / nt.BytesPerFileRecordSegment;
            m_mftToRecords.push_back(std::vector<uint32_t>((size_t)maxMft + 100000, 0xFFFFFFFF));
            std::vector<uint8_t> r0(nt.BytesPerFileRecordSegment); LARGE_INTEGER offs; offs.QuadPart = nt.MftStartLcn.QuadPart * nt.BytesPerCluster;
            if (!SetFilePointerEx(d.Handle, offs, NULL, FILE_BEGIN) || !ReadFile(d.Handle, r0.data(), nt.BytesPerFileRecordSegment, &cb, NULL)) continue;
            MFT_RECORD_HEADER* h0 = (MFT_RECORD_HEADER*)r0.data(); uint32_t ao0 = h0->AttributeOffset;
            while (ao0 + sizeof(MFT_ATTRIBUTE) < nt.BytesPerFileRecordSegment) {
                MFT_ATTRIBUTE* a = (MFT_ATTRIBUTE*)&r0[ao0];
                if (a->Type == 0x80) {
                    struct MFT_NR { MFT_ATTRIBUTE h; uint64_t v0, v1; uint16_t ro; }* nr = (MFT_NR*)a;
                    uint8_t* rl = (uint8_t*)a + nr->ro; int64_t curLcn = 0; uint32_t curMft = 0;
                    while (*rl) {
                        uint8_t hb = *rl++; int ls = hb & 0xF, os = hb >> 4;
                        uint64_t len = 0; for (int j=0; j<ls; ++j) len |= (uint64_t)(*rl++) << (j*8);
                        int64_t o = 0; for (int j=0; j<os; ++j) o |= (int64_t)(*rl++) << (j*8);
                        if (os>0 && (o >> (os*8-1))&1) for (int j=os; j<8; ++j) o |= (int64_t)0xFF << (j*8);
                        curLcn += o; LARGE_INTEGER eo; eo.QuadPart = curLcn * nt.BytesPerCluster;
                        SetFilePointerEx(d.Handle, eo, NULL, FILE_BEGIN); uint64_t bt = len * nt.BytesPerCluster;
                        std::vector<uint8_t> eb(1024*1024); DWORD br;
                        for (uint64_t r=0; r<bt; r+=br) {
                            if (!ReadFile(d.Handle, eb.data(), (DWORD)min(bt-r, 1024*1024ULL), &br, NULL) || !br) break;
                            for (uint32_t k=0; k+nt.BytesPerFileRecordSegment <= br; k+=nt.BytesPerFileRecordSegment) {
                                uint32_t tm = curMft++; MFT_RECORD_HEADER* rh = (MFT_RECORD_HEADER*)&eb[k];
                                if (rh->Magic != 0x454C4946 || !(rh->Flags & 0x01)) continue;
                                uint32_t ao = rh->AttributeOffset;
                                while (ao + sizeof(MFT_ATTRIBUTE) < nt.BytesPerFileRecordSegment) {
                                    MFT_ATTRIBUTE* aa = (MFT_ATTRIBUTE*)&eb[k+ao];
                                    if (aa->Type == 0x30) {
                                        MFT_FILE_NAME* fn = (MFT_FILE_NAME*)&eb[k+ao+((MFT_RESIDENT_ATTRIBUTE*)aa)->ValueOffset];
                                        if (fn->NameNamespace != 2) {
                                            std::wstring ws(fn->Name, fn->NameLength); uint32_t off; auto it = sMap.find(ws);
                                            if (it != sMap.end()) off = it->second; else { off = m_pool.AddString(ws); sMap[ws] = off; }
                                            FileRecord rec = { off, (uint32_t)(fn->ParentDirectory & 0xFFFFFFFFFFFFLL), tm, fn->DataSize, fn->LastWriteTime, (uint16_t)fn->FileAttributes, rh->SequenceNumber, (uint16_t)(fn->ParentDirectory >> 48), i };
                                            if (rh->Flags & 0x02) rec.Attributes |= 0x10;
                                            uint32_t ri = (uint32_t)m_records.size(); m_records.push_back(rec);
                                            if (tm < m_mftToRecords[i].size()) m_mftToRecords[i][tm] = ri;
                                            total++; if (total % 10000 == 0) m_status = L"Indexing... " + std::to_wstring(total) + L" items";
                                            break;
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
        SaveIndex(L"index.dat");
    }
    m_status = L"Ready - " + std::to_wstring(m_records.size()) + L" items";
    m_ready = true; Search(""); MonitorChanges();
}

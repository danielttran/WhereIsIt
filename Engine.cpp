#include "framework.h"
#include "Engine.h"
#include <iostream>
#include <debugapi.h>
#include <string>
#include <fstream>
#include <algorithm>
#include <unordered_map>
#include <shlobj.h>
#include <regex>
#include <cctype>
#include <cwctype>
#include <functional>

// --- Logger Implementation ---
bool Logger::m_enabled = true; 

void Logger::Log(const std::wstring& message) {
    if (!m_enabled) return;
    
    auto now = std::chrono::system_clock::now();
    auto in_time_t = std::chrono::system_clock::to_time_t(now);
    struct tm buf;
    localtime_s(&buf, &in_time_t);
    
    wchar_t timeStr[32];
    wcsftime(timeStr, 32, L"%Y-%m-%d %H:%M:%S", &buf);
    
    std::wstring out = std::wstring(timeStr) + L" - " + message + L"\n";
    OutputDebugStringW(out.c_str());
}

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


static std::string ToLowerAscii(std::string value) {
    for (char& c : value) c = (char)g_ToLowerLookup[(unsigned char)c];
    return value;
}

static bool FastContains(const std::string& haystack, const std::string& needle, bool caseSensitive) {
    if (needle.empty()) return true;
    if (caseSensitive) return haystack.find(needle) != std::string::npos;
    return FastContainsIgnoreCase(haystack.c_str(), needle.c_str());
}

static bool IsWordBoundary(const std::string& text, size_t pos) {
    if (pos >= text.size()) return true;
    unsigned char c = (unsigned char)text[pos];
    return !(std::isalnum(c) || c == '_');
}

static bool ContainsWholeWord(const std::string& haystack, const std::string& needle, bool caseSensitive) {
    if (needle.empty()) return true;
    std::string hay = haystack;
    std::string ndl = needle;
    if (!caseSensitive) {
        hay = ToLowerAscii(hay);
        ndl = ToLowerAscii(ndl);
    }
    size_t pos = 0;
    while ((pos = hay.find(ndl, pos)) != std::string::npos) {
        bool left = (pos == 0) || IsWordBoundary(hay, pos - 1);
        bool right = (pos + ndl.size() >= hay.size()) || IsWordBoundary(hay, pos + ndl.size());
        if (left && right) return true;
        ++pos;
    }
    return false;
}

static std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return std::string();
    int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, NULL, 0, NULL, NULL);
    if (size <= 1) return std::string();
    std::string out((size_t)size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, out.data(), size, NULL, NULL);
    out.pop_back();
    return out;
}

enum class QueryNodeType { Term, And, Or, Not };

struct QueryNode {
    QueryNodeType Type;
    std::string Term;
    std::unique_ptr<QueryNode> Left;
    std::unique_ptr<QueryNode> Right;
};

struct QueryConfig {
    bool CaseSensitive = false;
    bool RegexMode = false;
    bool WholeWord = false;
    bool SortDescending = false;
    QuerySortKey SortKey = QuerySortKey::Name;
};

struct QueryPlan {
    QueryConfig Config;
    std::vector<std::string> Tokens;
    std::unique_ptr<QueryNode> Root;
    std::vector<std::regex> CompiledRegex;
    bool Success = true;
    std::wstring ErrorMessage;
};

static bool ParseBool(const std::string& raw) {
    std::string value = ToLowerAscii(raw);
    return value == "1" || value == "true" || value == "yes" || value == "on";
}

static bool ParseSizeBytes(const std::string& raw, uint64_t& out) {
    std::string text = ToLowerAscii(raw);
    uint64_t mult = 1;
    if (text.size() > 2 && text.substr(text.size() - 2) == "kb") { mult = 1024; text.resize(text.size() - 2); }
    else if (text.size() > 2 && text.substr(text.size() - 2) == "mb") { mult = 1024ULL * 1024ULL; text.resize(text.size() - 2); }
    else if (text.size() > 2 && text.substr(text.size() - 2) == "gb") { mult = 1024ULL * 1024ULL * 1024ULL; text.resize(text.size() - 2); }
    else if (!text.empty() && text.back() == 'b') text.pop_back();
    if (text.empty()) return false;
    for (char c : text) if (!std::isdigit((unsigned char)c)) return false;
    out = std::stoull(text) * mult;
    return true;
}

static bool ParseDateToFileTime(const std::string& raw, uint64_t& out) {
    int y = 0, m = 0, d = 0;
    if (sscanf_s(raw.c_str(), "%d-%d-%d", &y, &m, &d) != 3) return false;
    SYSTEMTIME st = { 0 }; st.wYear = (WORD)y; st.wMonth = (WORD)m; st.wDay = (WORD)d;
    FILETIME ft;
    if (!SystemTimeToFileTime(&st, &ft)) return false;
    out = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    return true;
}

static std::vector<std::string> TokenizeQuery(const std::string& query) {
    std::vector<std::string> tokens;
    std::string current;
    bool inQuote = false;
    for (char c : query) {
        if (c == '"') {
            inQuote = !inQuote;
            if (!inQuote && !current.empty()) { tokens.push_back(current); current.clear(); }
            continue;
        }
        if (!inQuote && (c == '(' || c == ')')) {
            if (!current.empty()) { tokens.push_back(current); current.clear(); }
            tokens.emplace_back(1, c);
            continue;
        }
        if (!inQuote && std::isspace((unsigned char)c)) {
            if (!current.empty()) { tokens.push_back(current); current.clear(); }
            continue;
        }
        current.push_back(c);
    }
    if (!current.empty()) tokens.push_back(current);
    return tokens;
}

class QueryParser {
public:
    explicit QueryParser(const std::vector<std::string>& tokens) : m_tokens(tokens) {}
    std::unique_ptr<QueryNode> Parse(bool& success, std::wstring& error) { 
        m_pos = 0; m_error = L""; m_success = true;
        auto node = ParseOr(); 
        if (m_pos < m_tokens.size() && m_success) {
            m_success = false; m_error = L"Unexpected token: " + Utf8ToWide(m_tokens[m_pos]);
        }
        success = m_success; error = m_error;
        return node; 
    }
private:
    std::wstring Utf8ToWide(const std::string& s) {
        int sz = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, NULL, 0);
        if (sz <= 1) return L"";
        std::wstring res(sz - 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, &res[0], sz);
        return res;
    }
    std::unique_ptr<QueryNode> ParseOr() {
        auto node = ParseAnd();
        if (!node) return nullptr;
        while (Match("or")) {
            auto rhs = ParseAnd();
            if (!rhs) { m_success = false; m_error = L"Missing operand after 'OR'"; return node; }
            auto n = std::make_unique<QueryNode>(); n->Type = QueryNodeType::Or; n->Left = std::move(node); n->Right = std::move(rhs); node = std::move(n);
        }
        return node;
    }
    std::unique_ptr<QueryNode> ParseAnd() {
        auto node = ParseUnary();
        if (!node) return nullptr;
        while (true) {
            if (Match("and")) {
                auto rhs = ParseUnary();
                if (!rhs) { m_success = false; m_error = L"Missing operand after 'AND'"; return node; }
                auto n = std::make_unique<QueryNode>(); n->Type = QueryNodeType::And; n->Left = std::move(node); n->Right = std::move(rhs); node = std::move(n);
            } else if (CanImplicitAnd()) {
                auto rhs = ParseUnary();
                if (!rhs) break;
                auto n = std::make_unique<QueryNode>(); n->Type = QueryNodeType::And; n->Left = std::move(node); n->Right = std::move(rhs); node = std::move(n);
            } else break;
        }
        return node;
    }
    std::unique_ptr<QueryNode> ParseUnary() {
        if (Match("not")) {
            auto operand = ParseUnary();
            if (!operand) { m_success = false; m_error = L"Missing operand after 'NOT'"; return nullptr; }
            auto n = std::make_unique<QueryNode>(); n->Type = QueryNodeType::Not; n->Left = std::move(operand); return n;
        }
        if (Peek() == "(") {
            ++m_pos;
            auto n = ParseOr();
            if (Peek() != ")") { m_success = false; m_error = L"Missing ')'"; return n; }
            ++m_pos;
            return n;
        }
        std::string term = Consume();
        if (term.empty() || term == ")") return nullptr;
        auto n = std::make_unique<QueryNode>();
        n->Type = QueryNodeType::Term;
        n->Term = term;
        return n;
    }
    bool CanImplicitAnd() const {
        if (m_pos >= m_tokens.size()) return false;
        std::string t = ToLowerAscii(m_tokens[m_pos]);
        return t != ")" && t != "or" && t != "and";
    }
    bool Match(const char* op) {
        if (m_pos >= m_tokens.size()) return false;
        if (ToLowerAscii(m_tokens[m_pos]) == op) { ++m_pos; return true; }
        return false;
    }
    std::string Peek() const { return (m_pos < m_tokens.size()) ? m_tokens[m_pos] : std::string(); }
    std::string Consume() { return (m_pos < m_tokens.size()) ? m_tokens[m_pos++] : std::string(); }
    const std::vector<std::string>& m_tokens;
    size_t m_pos = 0;
    bool m_success = true;
    std::wstring m_error;
};

static QueryPlan BuildQueryPlan(const std::string& rawQuery) {
    QueryPlan plan;
    auto tokens = TokenizeQuery(rawQuery);
    std::vector<std::string> exprTokens;
    for (const auto& token : tokens) {
        std::string low = ToLowerAscii(token);
        if (low.rfind("case:", 0) == 0) { plan.Config.CaseSensitive = ParseBool(token.substr(5)); continue; }
        if (low.rfind("regex:", 0) == 0) { plan.Config.RegexMode = ParseBool(token.substr(6)); continue; }
        if (low.rfind("word:", 0) == 0) { plan.Config.WholeWord = ParseBool(token.substr(5)); continue; }
        if (low.rfind("sort:", 0) == 0) {
            std::string key = low.substr(5);
            if (key == "path") plan.Config.SortKey = QuerySortKey::Path;
            else if (key == "size") plan.Config.SortKey = QuerySortKey::Size;
            else if (key == "date") plan.Config.SortKey = QuerySortKey::Date;
            else plan.Config.SortKey = QuerySortKey::Name;
            continue;
        }
        if (low == "desc" || low == "sort:desc") { plan.Config.SortDescending = true; continue; }
        if (low == "asc" || low == "sort:asc") { plan.Config.SortDescending = false; continue; }
        exprTokens.push_back(token);
    }
    plan.Tokens = exprTokens;
    QueryParser parser(exprTokens);
    plan.Root = parser.Parse(plan.Success, plan.ErrorMessage);

    if (plan.Success && plan.Config.RegexMode) {
        std::regex_constants::syntax_option_type options = std::regex_constants::ECMAScript;
        if (!plan.Config.CaseSensitive) options |= std::regex_constants::icase;
        for (const auto& token : exprTokens) {
            std::string low = ToLowerAscii(token);
            if (low == "and" || low == "or" || low == "not" || low == "(" || low == ")" || token.find(':') != std::string::npos) continue;
            try { 
                if (token.length() > 1000) throw std::runtime_error("Regex too long");
                plan.CompiledRegex.emplace_back(token, options); 
            }
            catch (const std::regex_error& e) {
                plan.Success = false;
                int sz = MultiByteToWideChar(CP_UTF8, 0, e.what(), -1, NULL, 0);
                std::wstring msg(sz, L'\0'); MultiByteToWideChar(CP_UTF8, 0, e.what(), -1, &msg[0], sz);
                plan.ErrorMessage = L"Regex Error: " + msg;
                break;
            }
            catch (...) {
                plan.Success = false;
                plan.ErrorMessage = L"Unknown Regex Error";
                break;
            }
        }
    }
    return plan;
}

// --- StringPool: Minimal RAM for Filenames ---

StringPool::StringPool(size_t initialCapacity) { 
    if (initialCapacity > 0) m_pool.reserve(initialCapacity); 
    m_pool.push_back('\0'); 
}

void StringPool::Clear() { 
    m_pool.clear(); 
    m_pool.push_back('\0'); 
}

void StringPool::LoadRawData(const char* data, size_t size) { 
    m_pool.assign(data, data + size); 
}

uint32_t StringPool::AddRawData(const char* data, size_t size) {
    if (size == 0) return 0;
    uint32_t offset = (uint32_t)m_pool.size();
    m_pool.insert(m_pool.end(), data, data + size);
    return offset;
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

IndexingEngine::IndexingEngine() : m_running(false), m_ready(false), m_pool(20 * 1024 * 1024), m_isSearchRequested(false), m_isSortOnlyRequested(false), m_resultsUpdated(false) {
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

void IndexingEngine::CloseAllDriveHandles() {
    for (auto& d : m_drives) {
        if (d.VolumeHandle != INVALID_HANDLE_VALUE) {
            CloseHandle(d.VolumeHandle);
            d.VolumeHandle = INVALID_HANDLE_VALUE;
        }
    }
}

void IndexingEngine::Stop() {
    m_running = false;
    m_searchEvent.notify_all();
    if (m_mainWorker.joinable()) m_mainWorker.join();
    if (m_searchWorker.joinable()) m_searchWorker.join();
    CloseAllDriveHandles();
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
    std::wstring tmpPath = filePath + L".tmp";
    std::ofstream out(tmpPath, std::ios::binary | std::ios::trunc); if (!out) return false;
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
    out.flush(); if (!out.good()) { out.close(); DeleteFileW(tmpPath.c_str()); return false; }
    out.close(); if (!out.good()) { DeleteFileW(tmpPath.c_str()); return false; }
    if (!MoveFileExW(tmpPath.c_str(), filePath.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(tmpPath.c_str()); return false;
    }
    return true;
}

bool IndexingEngine::LoadIndex(const std::wstring& filePath) {
    auto clearState = [this]() {
        m_records.clear(); m_mftLookupTables.clear(); m_pool.Clear();
    };
    auto readExact = [](std::ifstream& stream, char* buffer, size_t bytes) {
        stream.read(buffer, (std::streamsize)bytes);
        return stream.good() || (stream.eof() && stream.gcount() == (std::streamsize)bytes);
    };

    std::ifstream in(filePath, std::ios::binary); 
    if (!in) {
        Logger::Log(L"[WhereIsIt] index.dat not found or could not be opened.");
        return false;
    }
    struct IndexHeader { uint32_t Magic, Version, DriveCount, RecordCount, PoolSize; } h; 
    if (!readExact(in, (char*)&h, sizeof(h))) { 
        Logger::Log(L"[WhereIsIt] Failed to read index header.");
        clearState(); return false; 
    }
    if (h.Magic != 0x54494957 || h.Version != 5) { 
        Logger::Log(L"[WhereIsIt] Index magic/version mismatch.");
        clearState(); return false; 
    }
    if (h.DriveCount == 0 || h.DriveCount > 64 || h.RecordCount > 100000000 || h.PoolSize == 0 || h.PoolSize > (512u * 1024u * 1024u)) {
        Logger::Log(L"[WhereIsIt] Index header contains invalid values.");
        clearState(); return false;
    }

    std::vector<DriveContext> tempDrives;
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        struct DriveInfoBin { wchar_t Letter[4]; uint32_t Serial; uint64_t LastUsn; } di; 
        if (!readExact(in, (char*)&di, sizeof(di))) { 
            Logger::Log(L"[WhereIsIt] Failed to read drive info from index.");
            clearState(); return false; 
        }
        if (FetchVolumeSerialNumber(di.Letter) != di.Serial) { 
            Logger::Log(L"[WhereIsIt] Drive serial number mismatch for " + std::wstring(di.Letter));
            clearState(); return false; 
        }
        int utf8Len = WideCharToMultiByte(CP_UTF8, 0, di.Letter, -1, NULL, 0, NULL, NULL);
        if (utf8Len <= 1) { clearState(); return false; }
        std::string letterUTF8(utf8Len - 1, '\0');
        WideCharToMultiByte(CP_UTF8, 0, di.Letter, -1, &letterUTF8[0], utf8Len, NULL, NULL);
        tempDrives.push_back({ di.Letter, letterUTF8, di.Serial, di.LastUsn, INVALID_HANDLE_VALUE, DriveFileSystem::Unknown });
    }
    std::vector<FileRecord> tempRecords;
    tempRecords.resize(h.RecordCount);
    if (!readExact(in, (char*)tempRecords.data(), (size_t)h.RecordCount * sizeof(FileRecord))) { 
        Logger::Log(L"[WhereIsIt] Failed to read file records from index.");
        clearState(); return false; 
    }

    std::vector<char> pd(h.PoolSize);
    if (!readExact(in, pd.data(), h.PoolSize)) { 
        Logger::Log(L"[WhereIsIt] Failed to read string pool from index.");
        clearState(); return false; 
    }

    for (const auto& rec : tempRecords) {
        if (rec.DriveIndex >= h.DriveCount) { clearState(); return false; }
        if (rec.NamePoolOffset >= h.PoolSize) { clearState(); return false; }
    }

    std::vector<std::vector<uint32_t>> tempLookup;
    tempLookup.resize(h.DriveCount);
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        uint32_t sz;
        if (!readExact(in, (char*)&sz, sizeof(uint32_t))) { 
            Logger::Log(L"[WhereIsIt] Failed to read lookup table size from index.");
            clearState(); return false; 
        }
        if (sz > 100000000) { clearState(); return false; }
        tempLookup[i].resize(sz);
        if (sz > 0 && !readExact(in, (char*)tempLookup[i].data(), (size_t)sz * sizeof(uint32_t))) { 
            Logger::Log(L"[WhereIsIt] Failed to read lookup table data from index.");
            clearState(); return false; 
        }
    }
    m_drives = std::move(tempDrives);
    m_records = std::move(tempRecords);
    m_pool.LoadRawData(pd.data(), h.PoolSize);
    m_mftLookupTables = std::move(tempLookup);
    return true;
}

void IndexingEngine::SetIndexScopeConfig(const IndexScopeConfig& config) {
    std::lock_guard<std::mutex> lock(m_scopeConfigMutex);
    m_scopeConfig = config;
}

IndexingEngine::IndexScopeConfig IndexingEngine::GetIndexScopeConfig() const {
    std::lock_guard<std::mutex> lock(m_scopeConfigMutex);
    return m_scopeConfig;
}

bool IndexingEngine::WildcardMatchI(const wchar_t* pattern, const wchar_t* text) {
    if (!pattern || !text) return false;
    const wchar_t *cp = nullptr, *mp = nullptr;
    while (*text) {
        if (*pattern == L'*') {
            if (!*++pattern) return true;
            mp = pattern; cp = text + 1;
        } else if (*pattern == L'?' || std::towlower(*pattern) == std::towlower(*text)) {
            pattern++; text++;
        } else if (mp) {
            pattern = mp; text = cp++;
        } else return false;
    }
    while (*pattern == L'*') pattern++;
    return !*pattern;
}

static bool WildcardMatchIAscii(const char* pattern, const char* text) {
    if (!pattern || !text) return false;
    const char *cp = nullptr, *mp = nullptr;
    while (*text) {
        if (*pattern == '*') {
            if (!*++pattern) return true;
            mp = pattern; cp = text + 1;
        } else if (*pattern == '?' || g_ToLowerLookup[(unsigned char)*pattern] == g_ToLowerLookup[(unsigned char)*text]) {
            pattern++; text++;
        } else if (mp) {
            pattern = mp; text = cp++;
        } else return false;
    }
    while (*pattern == '*') pattern++;
    return !*pattern;
}

bool IndexingEngine::IsRootEnabled(const std::wstring& root) const {
    std::lock_guard<std::mutex> lock(m_scopeConfigMutex);
    if (m_scopeConfig.IncludeRoots.empty()) return true;
    for (const auto& allowed : m_scopeConfig.IncludeRoots) {
        if (_wcsnicmp(root.c_str(), allowed.c_str(), allowed.size()) == 0) return true;
    }
    return false;
}

bool IndexingEngine::IsPathIncluded(const std::wstring& path) const {
    std::lock_guard<std::mutex> lock(m_scopeConfigMutex);
    for (const auto& pattern : m_scopeConfig.ExcludePathPatterns) {
        if (!pattern.empty() && WildcardMatchI(pattern.c_str(), path.c_str())) return false;
    }
    return true;
}

FileRecord IndexingEngine::GetRecord(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    if (recordIdx < m_records.size()) return m_records[recordIdx];
    return {};
}

std::wstring IndexingEngine::GetFullPath(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    return GetFullPathInternal(recordIdx);
}

std::wstring IndexingEngine::GetFullPathInternal(uint32_t recordIdx) const {
    if (recordIdx >= (uint32_t)m_records.size()) return L"";
    std::vector<const char*> parts;
    parts.reserve(32);
    uint32_t cur = recordIdx;
    uint8_t di = m_records[recordIdx].DriveIndex;
    if (di >= (uint8_t)m_drives.size() || di >= (uint8_t)m_mftLookupTables.size()) return L"";

    std::unordered_set<uint32_t> visited;
    while (parts.size() < 1024) { // Path depth limit guard
        if (!visited.insert(cur).second) break; // Cycle detection
        const FileRecord& r = m_records[cur]; 
        const char* name = m_pool.GetString(r.NamePoolOffset);
        if (name[0] != '.' || name[1] != '\0') {
            parts.push_back(name);
        }
        if (r.ParentMftIndex < 5 || r.ParentMftIndex >= (uint32_t)m_mftLookupTables[di].size()) break;
        uint32_t pi = m_mftLookupTables[di][r.ParentMftIndex];
        if (pi == 0xFFFFFFFF || pi >= (uint32_t)m_records.size() || m_records[pi].MftSequence != r.ParentSequence) break;
        if (pi == cur) break; cur = pi;
    }
    
    std::string pa;
    for (auto it = parts.rbegin(); it != parts.rend(); ++it) {
        if (!pa.empty()) pa += "\\";
        pa += *it;
    }
    int sz = MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, NULL, 0);
    if (sz > 0) { 
        std::wstring pw(sz - 1, L'\0'); 
        MultiByteToWideChar(CP_UTF8, 0, pa.c_str(), -1, &pw[0], sz); 
        return m_drives[di].Letter + pw; 
    }
    return m_drives[di].Letter;
}

std::wstring IndexingEngine::GetParentPath(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    return GetParentPathInternal(recordIdx);
}

std::wstring IndexingEngine::GetParentPathInternal(uint32_t recordIdx) const {
    if (recordIdx >= (uint32_t)m_records.size()) return L"";
    const FileRecord& child = m_records[recordIdx]; uint8_t di = child.DriveIndex;
    if (di >= (uint8_t)m_drives.size() || di >= (uint8_t)m_mftLookupTables.size()) return L"";
    if (child.ParentMftIndex < 5 || child.ParentMftIndex >= (uint32_t)m_mftLookupTables[di].size()) return m_drives[di].Letter;
    uint32_t pi = m_mftLookupTables[di][child.ParentMftIndex];
    if (pi == 0xFFFFFFFF || pi >= (uint32_t)m_records.size() || m_records[pi].MftSequence != child.ParentSequence) return m_drives[di].Letter;
    return GetFullPathInternal(pi);
}

void IndexingEngine::Search(const std::string& q) {
    { 
        std::lock_guard<std::mutex> lock(m_searchSyncMutex); 
        m_pendingSearchQuery = q; 
        m_isSearchRequested = true; 
        m_searchGeneration++;
    }
    m_searchEvent.notify_one();
}

void IndexingEngine::Sort(QuerySortKey key, bool descending) {
    {
        std::lock_guard<std::mutex> lock(m_searchSyncMutex);
        m_currentSortKey = key;
        m_currentSortDescending = descending;
        m_isSortOnlyRequested = true;
        m_searchGeneration++;
    }
    m_searchEvent.notify_one();
}

void IndexingEngine::SearchThread() {
    std::function<bool(const QueryNode*, const QueryPlan&, const FileRecord&, const std::string&, const std::string&)> evaluateTerm;
    evaluateTerm = [this, &evaluateTerm](const QueryNode* node, const QueryPlan& plan, const FileRecord& rec, const std::string& name, const std::string& fullPath) -> bool {
        if (!node) return true;
        if (node->Type == QueryNodeType::And) return evaluateTerm(node->Left.get(), plan, rec, name, fullPath) && evaluateTerm(node->Right.get(), plan, rec, name, fullPath);
        if (node->Type == QueryNodeType::Or) return evaluateTerm(node->Left.get(), plan, rec, name, fullPath) || evaluateTerm(node->Right.get(), plan, rec, name, fullPath);
        if (node->Type == QueryNodeType::Not) return !evaluateTerm(node->Left.get(), plan, rec, name, fullPath);

        const std::string term = node->Term;
        if (term.empty()) return true;
        std::string low = ToLowerAscii(term);

        if (low.rfind("ext:", 0) == 0) {
            const std::string ext = low.substr(4);
            std::string nameLower = ToLowerAscii(name);
            size_t dot = nameLower.find_last_of('.');
            return dot != std::string::npos && dot + 1 < nameLower.size() && nameLower.substr(dot + 1) == ext;
        }
        if (low.rfind("path:", 0) == 0) return !fullPath.empty() && FastContains(fullPath, term.substr(5), false);
        if (low.rfind("attr:", 0) == 0 || low.rfind("attrib:", 0) == 0) {
            size_t split = term.find(':');
            std::string attr = split == std::string::npos ? std::string() : ToLowerAscii(term.substr(split + 1));
            if (attr == "dir") return (rec.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            if (attr == "file") return (rec.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
            if (attr == "hidden") return (rec.FileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0;
            if (attr == "system") return (rec.FileAttributes & FILE_ATTRIBUTE_SYSTEM) != 0;
            if (attr == "archive") return (rec.FileAttributes & FILE_ATTRIBUTE_ARCHIVE) != 0;
            if (attr == "readonly") return (rec.FileAttributes & FILE_ATTRIBUTE_READONLY) != 0;
            return false;
        }
        if (low.rfind("size:", 0) == 0) {
            std::string raw = term.substr(5);
            int cmp = 0;
            if (raw.rfind(">=", 0) == 0) { cmp = 2; raw = raw.substr(2); }
            else if (raw.rfind("<=", 0) == 0) { cmp = -2; raw = raw.substr(2); }
            else if (raw.rfind(">", 0) == 0) { cmp = 1; raw = raw.substr(1); }
            else if (raw.rfind("<", 0) == 0) { cmp = -1; raw = raw.substr(1); }
            else if (raw.rfind("=", 0) == 0) { raw = raw.substr(1); }
            uint64_t value = 0; if (!ParseSizeBytes(raw, value)) return false;
            if (cmp == 2) return rec.FileSize >= value;
            if (cmp == -2) return rec.FileSize <= value;
            if (cmp == 1) return rec.FileSize > value;
            if (cmp == -1) return rec.FileSize < value;
            return rec.FileSize == value;
        }
        if (low.rfind("date:", 0) == 0) {
            std::string raw = term.substr(5);
            int cmp = 0;
            if (raw.rfind(">=", 0) == 0) { cmp = 2; raw = raw.substr(2); }
            else if (raw.rfind("<=", 0) == 0) { cmp = -2; raw = raw.substr(2); }
            else if (raw.rfind(">", 0) == 0) { cmp = 1; raw = raw.substr(1); }
            else if (raw.rfind("<", 0) == 0) { cmp = -1; raw = raw.substr(1); }
            else if (raw.rfind("=", 0) == 0) { raw = raw.substr(1); }
            uint64_t value = 0; if (!ParseDateToFileTime(raw, value)) return false;
            if (cmp == 2) return rec.LastModified >= value;
            if (cmp == -2) return rec.LastModified <= value;
            if (cmp == 1) return rec.LastModified > value;
            if (cmp == -1) return rec.LastModified < value;
            return rec.LastModified == value;
        }

        if (plan.Config.RegexMode && !plan.CompiledRegex.empty()) {
            for (const auto& rx : plan.CompiledRegex) {
                if (std::regex_search(name, rx)) return true;
                if (!fullPath.empty() && std::regex_search(fullPath, rx)) return true;
            }
            return false;
        }

        // DEFAULT SEARCH WITH WILDCARDS AND PATHS
        size_t lastSlash = term.find_last_of("\\/");
        if (lastSlash != std::string::npos) {
            // Path-based search
            std::string dirPart = term.substr(0, lastSlash + 1);
            std::string filePart = term.substr(lastSlash + 1);

            if (!fullPath.empty() && !FastContains(fullPath, dirPart, plan.Config.CaseSensitive)) return false;
            if (filePart.empty()) return true;

            if (filePart.find_first_of("*?") != std::string::npos) {
                return WildcardMatchIAscii(filePart.c_str(), name.c_str());
            }
            return FastContains(name, filePart, plan.Config.CaseSensitive);
        }

        if (term.find_first_of("*?") != std::string::npos) {
            return WildcardMatchIAscii(term.c_str(), name.c_str());
        }
        
        if (plan.Config.WholeWord) return ContainsWholeWord(name, term, plan.Config.CaseSensitive);
        return FastContains(name, term, plan.Config.CaseSensitive);
    };

    auto queryNeedsPath = [](const QueryNode* node) -> bool {
        std::function<bool(const QueryNode*)> check = [&](const QueryNode* n) -> bool {
            if (!n) return false;
            if (n->Type == QueryNodeType::Term) {
                std::string low = ToLowerAscii(n->Term);
                if (low.rfind("path:", 0) == 0) return true;
                if (n->Term.find_first_of("\\/") != std::string::npos) return true;
                return false;
            }
            return check(n->Left.get()) || check(n->Right.get());
        };
        return check(node);
    };

    std::string query;
    while (m_running) {
        QuerySortKey sortKey;
        bool sortDescending;
        bool sortOnly;
        {
            std::unique_lock<std::mutex> lock(m_searchSyncMutex);
            m_searchEvent.wait(lock, [this] { return !m_running || m_isSearchRequested || m_isSortOnlyRequested; });
            if (!m_running) break;
            query = m_pendingSearchQuery;
            sortKey = m_currentSortKey;
            sortDescending = m_currentSortDescending;
            sortOnly = m_isSortOnlyRequested.exchange(false);
            m_isSearchRequested = false;
        }

        auto results = std::make_shared<std::vector<uint32_t>>();
        if (sortOnly) {
            {
                std::lock_guard<std::mutex> lock(m_resultBufferMutex);
                if (m_currentResults) *results = *m_currentResults;
            }
            Logger::Log(L"[WhereIsIt] SearchThread: Sort-only requested. Re-sorting existing results.");
        } else {
            wchar_t debugBuf[256];
            swprintf_s(debugBuf, L"[WhereIsIt] SearchThread: Processing query '%S'. Records to search: %zu\n", query.c_str(), m_records.size());
            Logger::Log(debugBuf);

            QueryPlan plan = BuildQueryPlan(query);
            if (!plan.Success) {
                m_status = L"Query Error: " + plan.ErrorMessage;
                std::lock_guard<std::mutex> lock(m_resultBufferMutex);
                m_currentResults = results; // Empty results
                m_resultsUpdated = true;
                continue;
            }

            if (query.find("sort:") != std::string::npos || query.find("desc") != std::string::npos || query.find("asc") != std::string::npos) {
                sortKey = plan.Config.SortKey;
                sortDescending = plan.Config.SortDescending;
            }

            bool needsPath = queryNeedsPath(plan.Root.get()) || plan.Config.RegexMode;
            std::unordered_map<uint32_t, std::string> pathCache;
            auto pathFor = [this, &pathCache](uint32_t idx) {
                auto it = pathCache.find(idx);
                if (it != pathCache.end()) return it->second;
                std::string p = WideToUtf8(GetFullPathInternal(idx));
                pathCache.emplace(idx, p);
                return p;
            };

            {
                std::shared_lock<std::shared_mutex> dataLock(m_dataMutex);
                results->reserve(m_records.size());
                if (query.empty()) {
                    for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) {
                        if (m_records[i].MftIndex != 0xFFFFFFFF) results->push_back(i);
                    }
                } else {
                    for (uint32_t i = 0; i < (uint32_t)m_records.size(); ++i) {
                        if (m_isSearchRequested) break;
                        const auto& rec = m_records[i];
                        if (rec.MftIndex == 0xFFFFFFFF) continue;
                        std::string name = m_pool.GetString(rec.NamePoolOffset);
                        std::string path;
                        if (needsPath) path = pathFor(i);
                        if (evaluateTerm(plan.Root.get(), plan, rec, name, path)) results->push_back(i);
                    }
                }
            }
        }

        if (m_isSearchRequested) continue;

        std::unordered_map<uint32_t, std::string> pathCache;
        auto pathFor = [this, &pathCache](uint32_t idx) {
            auto it = pathCache.find(idx);
            if (it != pathCache.end()) return it->second;
            std::string p = WideToUtf8(GetFullPathInternal(idx));
            pathCache.emplace(idx, p);
            return p;
        };

        // Pre-calculate paths ONLY if sorting by path to avoid O(N log N) reconstructions
        if (sortKey == QuerySortKey::Path) {
            std::shared_lock<std::shared_mutex> dataLock(m_dataMutex);
            for (uint32_t idx : *results) pathFor(idx);
        }

        {
            std::shared_lock<std::shared_mutex> dataLock(m_dataMutex);
            std::sort(results->begin(), results->end(), [this, sortKey, sortDescending, &pathFor](uint32_t a, uint32_t b) {
                const auto& ra = m_records[a];
                const auto& rb = m_records[b];
                
                auto cmpName = [this, &ra, &rb]() { 
                    return FastCompareIgnoreCase(m_pool.GetString(ra.NamePoolOffset), m_pool.GetString(rb.NamePoolOffset)); 
                };

                int primary = 0;
                if (sortKey == QuerySortKey::Path) {
                    std::string pa = pathFor(a), pb = pathFor(b);
                    if (pa < pb) primary = -1; else if (pa > pb) primary = 1;
                } else if (sortKey == QuerySortKey::Size) {
                    if (ra.FileSize < rb.FileSize) primary = -1; else if (ra.FileSize > rb.FileSize) primary = 1;
                } else if (sortKey == QuerySortKey::Date) {
                    if (ra.LastModified < rb.LastModified) primary = -1; else if (ra.LastModified > rb.LastModified) primary = 1;
                }

                if (primary == 0) primary = cmpName();
                if (primary == 0) primary = (a < b ? -1 : 1);
                
                return sortDescending ? (primary > 0) : (primary < 0);
            });
        }

        {
            std::lock_guard<std::mutex> lock(m_resultBufferMutex);
            m_currentResults = std::move(results);
            m_resultsUpdated = true;
        }
        Logger::Log(L"[WhereIsIt] SearchThread: Sort and update complete.");
    }
}

void IndexingEngine::HandleUsnJournalRecord(USN_RECORD_V2* r, uint8_t di) {
    uint32_t mftIdx = (uint32_t)(r->FileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t seq = (uint16_t)(r->FileReferenceNumber >> 48);
    uint32_t pIdx = (uint32_t)(r->ParentFileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t pSeq = (uint16_t)(r->ParentFileReferenceNumber >> 48);

    if (r->Reason & USN_REASON_FILE_DELETE) {
        std::unique_lock<std::shared_mutex> lock(m_dataMutex);
        if (mftIdx < (uint32_t)m_mftLookupTables[di].size()) {
            uint32_t idx = m_mftLookupTables[di][mftIdx];
            if (idx != 0xFFFFFFFF && m_records[idx].MftSequence == seq) { 
                m_records[idx].MftIndex = 0xFFFFFFFF; 
                m_mftLookupTables[di][mftIdx] = 0xFFFFFFFF; 
            }
        }
    }
    
    if (r->Reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME | USN_REASON_DATA_EXTEND | USN_REASON_DATA_TRUNCATION | USN_REASON_CLOSE)) {
        std::wstring name(r->FileName, r->FileNameLength/2);
        uint64_t fileSize = 0, lastMod = 0;
        
        {
            std::shared_lock<std::shared_mutex> lock(m_dataMutex);
            // Reconstruct path to get attributes. 
            // Note: Since we are in the middle of updates, the parent might not exist yet or might be stale.
            // But we can try to find the parent in our records.
            std::wstring fullPath;
            if (pIdx < (uint32_t)m_mftLookupTables[di].size()) {
                uint32_t parentRecIdx = m_mftLookupTables[di][pIdx];
                if (parentRecIdx != 0xFFFFFFFF) {
                    fullPath = GetFullPathInternal(parentRecIdx) + L"\\" + name;
                }
            }
            if (fullPath.empty()) fullPath = m_drives[di].Letter + name; // Fallback

            WIN32_FILE_ATTRIBUTE_DATA att;
            if (GetFileAttributesExW(fullPath.c_str(), GetFileExInfoStandard, &att)) {
                fileSize = ((uint64_t)att.nFileSizeHigh << 32) | att.nFileSizeLow;
                lastMod = ((uint64_t)att.ftLastWriteTime.dwHighDateTime << 32) | att.ftLastWriteTime.dwLowDateTime;
            }
        }
        
        std::unique_lock<std::shared_mutex> lock(m_dataMutex);
        FileRecord rec = { m_pool.AddString(name), pIdx, mftIdx, fileSize, lastMod, (uint16_t)r->FileAttributes, seq, pSeq, di };
        
        if (mftIdx >= (uint32_t)m_mftLookupTables[di].size()) {
            m_mftLookupTables[di].resize(mftIdx + 10000, 0xFFFFFFFF);
        }
        
        uint32_t existing = m_mftLookupTables[di][mftIdx];
        if (existing != 0xFFFFFFFF) {
            // Update existing record if sequence matches or is newer
            if (m_records[existing].MftSequence <= seq) {
                m_records[existing] = rec;
            }
        } else {
            uint32_t idx = (uint32_t)m_records.size();
            m_records.push_back(rec);
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
    Logger::Log(L"[WhereIsIt] Discovering drives...\n");
    const IndexScopeConfig cfg = GetIndexScopeConfig();
    wchar_t drvs[512]; GetLogicalDriveStringsW(512, drvs);
    wchar_t debugBuf[256];
    for (wchar_t* p = drvs; *p; p += wcslen(p) + 1) {
        UINT driveType = GetDriveTypeW(p);
        bool allowedType = (driveType == DRIVE_FIXED || driveType == DRIVE_REMOVABLE || driveType == DRIVE_CDROM);
        if (!allowedType && cfg.IndexNetworkDrives && driveType == DRIVE_REMOTE) allowedType = true;
        
        swprintf_s(debugBuf, L"[WhereIsIt] Checking drive %s (Type: %u, Allowed: %d)\n", p, (UINT)driveType, (int)allowedType);
        Logger::Log(debugBuf);

        if (!allowedType) continue;
        if (!IsRootEnabled(p)) {
            Logger::Log(L"[WhereIsIt] Drive root not enabled in config.\n");
            continue;
        }
        wchar_t fs[32] = { 0 };
        DriveFileSystem type = DriveFileSystem::Generic;
        if (GetVolumeInformationW(p, NULL, 0, NULL, NULL, NULL, fs, 32)) {
            if (_wcsicmp(fs, L"NTFS") == 0) type = DriveFileSystem::NTFS;
        }
        
        swprintf_s(debugBuf, L"[WhereIsIt] Drive %s FS: %s (Type: %d)\n", p, fs, (int)type);
        Logger::Log(debugBuf);

        std::wstring vp = L"\\\\.\\" + std::wstring(p, 2);
        HANDLE h = CreateFileW(vp.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
        if (h == INVALID_HANDLE_VALUE && type == DriveFileSystem::NTFS) {
            Logger::Log(L"[WhereIsIt] Failed to open volume handle for NTFS. Falling back to Generic.\n");
            type = DriveFileSystem::Generic; // Can't read MFT directly; fall back to directory scan
        }
        if (h != INVALID_HANDLE_VALUE || type == DriveFileSystem::Generic) {
            int utf8Len = WideCharToMultiByte(CP_UTF8, 0, p, -1, NULL, 0, NULL, NULL);
            std::string letterUTF8(utf8Len - 1, '\0');
            WideCharToMultiByte(CP_UTF8, 0, p, -1, &letterUTF8[0], utf8Len, NULL, NULL);
            m_drives.push_back({ p, letterUTF8, FetchVolumeSerialNumber(p), 0, h, type });
            Logger::Log(L"[WhereIsIt] Drive added to list.\n");
        }
    }
    swprintf_s(debugBuf, L"[WhereIsIt] Discovery complete. Total drives: %zu\n", m_drives.size());
    Logger::Log(debugBuf);
    return !m_drives.empty();
}

static bool IsRunningAsAdmin() {
    BOOL isAdmin = FALSE; PSID adminGroup;
    SID_IDENTIFIER_AUTHORITY ntAuthority = SECURITY_NT_AUTHORITY;
    if (AllocateAndInitializeSid(&ntAuthority, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &adminGroup)) {
        CheckTokenMembership(NULL, adminGroup, &isAdmin);
        FreeSid(adminGroup);
    }
    return isAdmin == TRUE;
}

void IndexingEngine::PerformFullDriveScan() {
    wchar_t debugBuf[256];
    swprintf_s(debugBuf, L"[WhereIsIt] Performing parallel full drive scan for %zu drives...\n", m_drives.size());
    Logger::Log(debugBuf);
    
    std::vector<DriveScanContext> contexts(m_drives.size());
    std::vector<std::thread> workers;
    std::atomic<size_t> totalFound{ 0 };
    std::atomic<int> activeWorkers{ 0 };

    for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
        auto& d = m_drives[i];
        contexts[i].DriveIndex = i;
        contexts[i].DriveLetter = d.Letter;
        contexts[i].VolumeHandle = d.VolumeHandle;
        contexts[i].Type = d.Type;
        
        activeWorkers++;
        workers.emplace_back([this, &ctx = contexts[i], &totalFound, &activeWorkers]() {
            if (ctx.Type == DriveFileSystem::NTFS) {
                ScanMftForDrive(ctx);
            } else {
                std::unordered_set<uint64_t> visitedDirs;
                ScanGenericDrive(ctx, ctx.DriveLetter, 0xFFFFFFFF, 0, visitedDirs);
            }
            totalFound += ctx.Records.size();
            activeWorkers--;
        });
    }

    // Progress reporting thread
    std::thread progressThread([this, &totalFound, &activeWorkers]() {
        while (activeWorkers > 0) {
            m_status = L"Indexing... " + FormatNumberWithCommas(totalFound.load()) + L" items";
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    });

    for (auto& t : workers) t.join();
    if (progressThread.joinable()) progressThread.join();

    {
        std::unique_lock<std::shared_mutex> lock(m_dataMutex);
        m_records.clear(); m_pool.Clear(); m_mftLookupTables.clear();
        m_mftLookupTables.resize(m_drives.size());

        for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
            auto& ctx = contexts[i];
            uint32_t poolOffsetShift = m_pool.AddRawData(ctx.Pool.GetRawData(), ctx.Pool.GetSize());
            uint32_t recordIndexShift = (uint32_t)m_records.size();

            m_mftLookupTables[i] = std::move(ctx.LookupTable);
            
            for (auto& rec : ctx.Records) {
                rec.NamePoolOffset += poolOffsetShift;
                if (ctx.Type != DriveFileSystem::NTFS) {
                    if (rec.ParentMftIndex != 0xFFFFFFFF) rec.ParentMftIndex += recordIndexShift;
                    uint32_t localIdx = rec.MftIndex; 
                    rec.MftIndex = recordIndexShift + localIdx;
                    if (localIdx >= m_mftLookupTables[i].size()) m_mftLookupTables[i].resize(localIdx + 1, 0xFFFFFFFF);
                    m_mftLookupTables[i][localIdx] = rec.MftIndex;
                }
                m_records.push_back(rec);
            }

            if (ctx.Type == DriveFileSystem::NTFS) {
                for (auto& val : m_mftLookupTables[i]) {
                    if (val != 0xFFFFFFFF) val += recordIndexShift;
                }
            }
            m_drives[i].LastProcessedUsn = ctx.LastProcessedUsn;
        }
    }
    Logger::Log(L"[WhereIsIt] Parallel drive scan and merge complete.\n");
}

void IndexingEngine::ScanGenericDrive(DriveScanContext& ctx, const std::wstring& rootPath, uint32_t pIdx, uint16_t pSeq, std::unordered_set<uint64_t>& visitedDirs) {
    struct StackEntry { std::wstring path; uint32_t pIdx; uint16_t pSeq; };
    std::vector<StackEntry> stack;
    stack.push_back({ rootPath, pIdx, pSeq });

    const IndexScopeConfig cfg = GetIndexScopeConfig();

    while (!stack.empty()) {
        StackEntry current = std::move(stack.back());
        stack.pop_back();

        if (!IsPathIncluded(current.path)) continue;

        HANDLE dirHandle = CreateFileW(current.path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
        if (dirHandle != INVALID_HANDLE_VALUE) {
            BY_HANDLE_FILE_INFORMATION info = { 0 };
            if (GetFileInformationByHandle(dirHandle, &info)) {
                uint64_t fileId = ((uint64_t)info.nFileIndexHigh << 32) | info.nFileIndexLow;
                if (!visitedDirs.insert(fileId).second) {
                    CloseHandle(dirHandle); continue;
                }
            }
            CloseHandle(dirHandle);
        }

        WIN32_FIND_DATAW fd;
        std::wstring searchPattern = current.path + L"*";
        HANDLE hFind = FindFirstFileExW(searchPattern.c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
        if (hFind == INVALID_HANDLE_VALUE) continue;

        do {
            if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
            std::wstring fullPath = current.path + fd.cFileName;
            if (!IsPathIncluded(fullPath)) continue;

            uint32_t myLocalIdx = (uint32_t)ctx.Records.size();
            FileRecord rec = { ctx.Pool.AddString(fd.cFileName), current.pIdx, myLocalIdx, ((uint64_t)fd.nFileSizeHigh << 32) | fd.nFileSizeLow, 
                              ((uint64_t)fd.ftLastWriteTime.dwHighDateTime << 32) | fd.ftLastWriteTime.dwLowDateTime, 
                              (uint16_t)fd.dwFileAttributes, 0, current.pSeq, ctx.DriveIndex };
            ctx.Records.push_back(rec);

            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                if (!cfg.FollowReparsePoints && (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) continue;
                stack.push_back({ fullPath + L"\\", myLocalIdx, 0 });
            }
        } while (FindNextFileW(hFind, &fd));
        FindClose(hFind);
    }
}

void IndexingEngine::ScanMftForDrive(DriveScanContext& ctx) {
    NTFS_VOLUME_DATA_BUFFER nt; DWORD cb; 
    if (!DeviceIoControl(ctx.VolumeHandle, FSCTL_GET_NTFS_VOLUME_DATA, NULL, 0, &nt, sizeof(nt), &cb, NULL)) return;
    USN_JOURNAL_DATA_V0 uj; if (DeviceIoControl(ctx.VolumeHandle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) ctx.LastProcessedUsn = uj.NextUsn;
    uint64_t maxMft = nt.MftValidDataLength.QuadPart / nt.BytesPerFileRecordSegment;
    ctx.LookupTable.assign((size_t)maxMft + 100000, 0xFFFFFFFF);
    std::vector<uint8_t> r0(nt.BytesPerFileRecordSegment); LARGE_INTEGER offs; offs.QuadPart = nt.MftStartLcn.QuadPart * nt.BytesPerCluster;
    if (!SetFilePointerEx(ctx.VolumeHandle, offs, NULL, FILE_BEGIN) || !ReadFile(ctx.VolumeHandle, r0.data(), nt.BytesPerFileRecordSegment, &cb, NULL)) return;
    MFT_RECORD_HEADER* h0 = (MFT_RECORD_HEADER*)r0.data(); uint32_t ao0 = h0->AttributeOffset;
    while (ao0 + sizeof(MFT_ATTRIBUTE) < nt.BytesPerFileRecordSegment) {
        MFT_ATTRIBUTE* a = (MFT_ATTRIBUTE*)&r0[ao0];
        if (a->Type == 0x80) {
            struct MFT_NR { MFT_ATTRIBUTE h; uint64_t startVcn, lastVcn; uint16_t runOffset; }* nr = (MFT_NR*)a;
            uint8_t* rl = (uint8_t*)a + nr->runOffset; int64_t curLcn = 0; uint32_t mftCounter = 0;
            std::vector<uint8_t> eb(1024*1024);
            while (*rl) {
                uint8_t hb = *rl++; int ls = hb & 0xF, os = hb >> 4;
                uint64_t len = 0; for (int j=0; j<ls; j++) len |= (uint64_t)(*rl++) << (j*8);
                int64_t runOff = 0; for (int j=0; j<os; j++) runOff |= (int64_t)(*rl++) << (j*8);
                if (os > 0 && (runOff >> (os*8-1))&1) for (int j=os; j<8; j++) runOff |= (int64_t)0xFF << (j*8);
                curLcn += runOff; LARGE_INTEGER eo; eo.QuadPart = curLcn * nt.BytesPerCluster;
                SetFilePointerEx(ctx.VolumeHandle, eo, NULL, FILE_BEGIN); uint64_t bt = len * nt.BytesPerCluster;
                DWORD br;
                for (uint64_t r=0; r<bt; r+=br) {
                    if (!ReadFile(ctx.VolumeHandle, eb.data(), (DWORD)min(bt-r, 1024*1024ULL), &br, NULL) || !br) break;
                    for (uint32_t k=0; k+nt.BytesPerFileRecordSegment <= br; k+=nt.BytesPerFileRecordSegment) {
                        uint32_t tm = mftCounter++; MFT_RECORD_HEADER* rh = (MFT_RECORD_HEADER*)&eb[k];
                        if (rh->Magic != 0x454C4946 || !(rh->Flags & 0x01)) continue;
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
                            if (aa->Type == 0xFFFFFFFF) break; 
                            if (aa->Type == 0x30 && !aa->NonResident) {
                                MFT_FILE_NAME* fn = (MFT_FILE_NAME*)&eb[k+ao+((MFT_RESIDENT_ATTRIBUTE*)aa)->ValueOffset];
                                if (fn->NameNamespace != 2) {
                                    FileRecord entry = { ctx.Pool.AddString(fn->Name, fn->NameLength), (uint32_t)(fn->ParentDirectory & 0xFFFFFFFFFFFFLL), tm, fn->DataSize, fn->LastWriteTime, (uint16_t)fn->FileAttributes, rh->SequenceNumber, (uint16_t)(fn->ParentDirectory >> 48), ctx.DriveIndex };
                                    if (rh->Flags & 0x02) entry.FileAttributes |= 0x10;
                                    uint32_t ri = (uint32_t)ctx.Records.size(); ctx.Records.push_back(entry);
                                    if (tm < (uint32_t)ctx.LookupTable.size()) ctx.LookupTable[tm] = ri;
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

void IndexingEngine::WorkerThread() {
    Logger::Log(L"====================================================================");
    Logger::Log(L"[WhereIsIt] NEW SESSION STARTED");
    if (IsRunningAsAdmin()) Logger::Log(L"[WhereIsIt] Running with ADMINISTRATOR privileges.");
    else Logger::Log(L"[WhereIsIt] Running with Standard User privileges (NTFS direct access disabled).");
    Logger::Log(L"====================================================================");
    Logger::Log(L"[WhereIsIt] Worker thread started.\n");
    m_ready = false;
    m_drives.clear(); 
    if (!DiscoverAllDrives()) { 
        Logger::Log(L"[WhereIsIt] No drives discovered. Exiting worker thread.\n");
        m_ready = true; return; 
    }
    
    if (LoadIndex(ResolveIndexSavePath())) {
        Logger::Log(L"[WhereIsIt] Index loaded. Performing USN Journal Catch-up...");
        for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
            auto& d = m_drives[i];
            wchar_t fs[32] = { 0 };
            if (GetVolumeInformationW(d.Letter.c_str(), NULL, 0, NULL, NULL, NULL, fs, 32))
                d.Type = (_wcsicmp(fs, L"NTFS") == 0) ? DriveFileSystem::NTFS : DriveFileSystem::Generic;
            
            if (d.Type == DriveFileSystem::NTFS) {
                std::wstring vp = L"\\\\.\\" + d.Letter.substr(0, 2);
                d.VolumeHandle = CreateFileW(vp.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
                USN_JOURNAL_DATA_V0 uj; DWORD cb;
                if (d.VolumeHandle != INVALID_HANDLE_VALUE && DeviceIoControl(d.VolumeHandle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) {
                    if ((uint64_t)uj.NextUsn > d.LastProcessedUsn) {
                        wchar_t logBuf[256];
                        swprintf_s(logBuf, L"[WhereIsIt] Drive %s has %llu new journal bytes. Catching up...", d.Letter.c_str(), (uint64_t)uj.NextUsn - d.LastProcessedUsn);
                        Logger::Log(logBuf);
                    }
                }
            }
        }
    } else {
        Logger::Log(L"[WhereIsIt] No index found or load failed. Performing parallel full scan.\n");
        PerformFullDriveScan();
        SaveIndex(ResolveIndexSavePath());
    }
    
    wchar_t debugBuf[256];
    swprintf_s(debugBuf, L"[WhereIsIt] Worker thread ready. Total records: %s. Triggering initial search.\n", FormatNumberWithCommas(m_records.size()).c_str());
    Logger::Log(debugBuf);

    m_status = L"Ready - " + FormatNumberWithCommas(m_records.size()) + L" items";
    m_ready = true;
    Search("");
    MonitorChanges();
}

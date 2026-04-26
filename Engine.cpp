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
#include <immintrin.h>
#include <intrin.h>

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

// Zero-alloc version: haystack and needle are both raw C-strings.
// needle must already be pre-lowercased (use g_ToLowerLookup).
// This is the hot-path version used in the fast-scan loop.
static bool FastContainsCIPtr(const char* haystack, const char* needleLow, size_t needleLen) {
    if (!needleLen) return true;
    const unsigned char first = (unsigned char)needleLow[0];
#if defined(__AVX2__)
    const char lowerFirst = (char)first;
    const char upperFirst = (lowerFirst >= 'a' && lowerFirst <= 'z') ? (char)(lowerFirst - 32) : lowerFirst;
    const __m256i vLower = _mm256_set1_epi8(lowerFirst);
    const __m256i vUpper = _mm256_set1_epi8(upperFirst);
#endif
    const char* p = haystack;
#if defined(__AVX2__)
    size_t remaining = strlen(haystack);  // pre-calculated once — avoids O(N²) strlen-in-loop
    while (remaining >= 32) {
        __m256i v = _mm256_loadu_si256((const __m256i*)p);
        __m256i eqLower = _mm256_cmpeq_epi8(v, vLower);
        __m256i eqUpper = _mm256_cmpeq_epi8(v, vUpper);
        unsigned mask = (unsigned)_mm256_movemask_epi8(_mm256_or_si256(eqLower, eqUpper));
        while (mask) {
            unsigned long bitScan = 0;
            _BitScanForward(&bitScan, mask);
            unsigned bit = (unsigned)bitScan;
            const char* h = p + bit;
            const char* n = needleLow;
            while (*h && *n && g_ToLowerLookup[(unsigned char)*h] == (unsigned char)*n) { ++h; ++n; }
            if (!*n) return true;
            mask &= (mask - 1);
        }
        p += 32;
        remaining -= 32;
    }
#endif
    for (; *p; ++p) {
        if (g_ToLowerLookup[(unsigned char)*p] == first) {
            const char *h = p, *n = needleLow;
            while (*h && *n && g_ToLowerLookup[(unsigned char)*h] == (unsigned char)*n) { ++h; ++n; }
            if (!*n) return true;
        }
    }
    return false;
}

static bool IsWordBoundary(const std::string& text, size_t pos) {
    if (pos >= text.size()) return true;
    unsigned char c = (unsigned char)text[pos];
    return !(std::isalnum(c) || c == '_');
}

static bool ContainsWholeWord(const std::string& haystack, const std::string& needle, bool caseSensitive) {
    // Alloc-free implementation: scan using the lookup table directly, no string copies.
    if (needle.empty()) return true;
    const size_t hLen = haystack.size(), nLen = needle.size();
    if (nLen > hLen) return false;
    for (size_t i = 0; i <= hLen - nLen; ++i) {
        bool match = true;
        for (size_t j = 0; j < nLen && match; ++j) {
            unsigned char hc = (unsigned char)haystack[i + j];
            unsigned char nc = (unsigned char)needle[j];
            match = caseSensitive ? (hc == nc) : (g_ToLowerLookup[hc] == g_ToLowerLookup[nc]);
        }
        if (match) {
            bool left  = (i == 0)            || IsWordBoundary(haystack, i - 1);
            bool right = (i + nLen >= hLen)  || IsWordBoundary(haystack, i + nLen);
            if (left && right) return true;
        }
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
    std::string Term;      // original term as typed
    std::string TermLower; // precomputed ToLowerAscii(Term) — avoids per-record allocation in hot loop
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

// ---------------------------------------------------------------------------
// Regex abstraction layer
// Phase A: wraps std::regex behind CompiledPattern::match() so that the
// implementation can be swapped for PCRE2/RE2 in Phase B without touching
// any call sites outside this struct.
// ---------------------------------------------------------------------------
struct CompiledPattern {
    std::regex re;
    CompiledPattern() = default;
    CompiledPattern(const std::string& pattern, std::regex_constants::syntax_option_type opts)
        : re(pattern, opts) {}
    // Returns true if the pattern matches anywhere in `text`.
    bool match(const std::string& text) const { return std::regex_search(text, re); }
};

struct QueryPlan {
    QueryConfig Config;
    std::vector<std::string> Tokens;
    std::unique_ptr<QueryNode> Root;
    std::vector<CompiledPattern> CompiledRegex;  // abstracted; swap internals for PCRE2 in Phase B
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

static uint32_t FileTimeToUnixEpochSeconds(uint64_t fileTime) {
    constexpr uint64_t kFileTimeTicksPerSecond = 10000000ULL;
    constexpr uint64_t kEpochDiffSeconds = 11644473600ULL;
    uint64_t seconds = fileTime / kFileTimeTicksPerSecond;
    if (seconds <= kEpochDiffSeconds) return 0;
    uint64_t epoch = seconds - kEpochDiffSeconds;
    return epoch > 0xFFFFFFFFULL ? 0xFFFFFFFFu : (uint32_t)epoch;
}

static uint64_t UnixEpochSecondsToFileTime(uint32_t epoch) {
    constexpr uint64_t kFileTimeTicksPerSecond = 10000000ULL;
    constexpr uint64_t kEpochDiffSeconds = 11644473600ULL;
    return (uint64_t(epoch) + kEpochDiffSeconds) * kFileTimeTicksPerSecond;
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
        n->TermLower = ToLowerAscii(term); // precompute once — shared across all 500k+ record evaluations
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

    if (!plan.Success) {
        // Fallback: if the query contains invalid syntax (e.g. unmatched parentheses),
        // treat the entire raw input as a single literal search term instead of throwing an error.
        plan.Success = true;
        plan.ErrorMessage.clear();
        plan.Root = std::make_unique<QueryNode>();
        plan.Root->Type = QueryNodeType::Term;
        std::string rawTerm;
        for (size_t i = 0; i < exprTokens.size(); ++i) {
            if (i > 0) rawTerm += " ";
            rawTerm += exprTokens[i];
        }
        plan.Root->Term = rawTerm;
        plan.Root->TermLower = ToLowerAscii(rawTerm);
    }

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
                std::wstring msg(sz - 1, L'\0'); MultiByteToWideChar(CP_UTF8, 0, e.what(), -1, &msg[0], sz);
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


SECURITY_ATTRIBUTES* GetPermissiveSA() {
    static SECURITY_ATTRIBUTES sa = { sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE };
    static bool initialized = false;
    if (!initialized) {
        PSECURITY_DESCRIPTOR pSD = nullptr;
        // D:(A;;GA;;;WD) = Allow Generic All to World (Everyone)
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;WD)", SDDL_REVISION_1, &pSD, nullptr)) {
            sa.lpSecurityDescriptor = pSD;
            initialized = true;
        }
    }
    return &sa;
}

// --- StringPool: Chunked Arena — O(1) alloc, zero realloc ---

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

        HANDLE hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, GetPermissiveSA(), PAGE_READWRITE,
            (DWORD)((allocSize + 32) >> 32), (DWORD)((allocSize + 32) & 0xFFFFFFFF), pMapName);        char* view = nullptr;
        if (hMap) {
            view = (char*)MapViewOfFile(hMap, FILE_MAP_WRITE, 0, 0, allocSize + 32);
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

uint32_t StringPool::AdoptChunksFrom(StringPool& other) {
    if (other.m_chunks.empty()) return (uint32_t)m_totalSize;
    size_t startChunkIdx = m_chunks.size();
    uint32_t shift = (uint32_t)(startChunkIdx * kChunkSize);
    
    for (auto& chunk : other.m_chunks) {
        m_chunks.push_back(chunk);
    }
    other.m_chunks.clear();
    
    m_chunkUsed = other.m_chunkUsed;
    other.m_chunkUsed = 0;
    m_totalSize = m_chunks.size() > 0 ? ((m_chunks.size() - 1) * kChunkSize + m_chunkUsed) : 0;
    other.m_totalSize = 0;
    
    return shift;
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


// --- IndexingEngine Implementation ---

IndexingEngine::IndexingEngine() : m_running(false), m_ready(false), m_pool(true), m_isSearchRequested(false), m_isSortOnlyRequested(false) {
    m_hDataMutex = CreateMutexW(GetPermissiveSA(), FALSE, L"Global\\WhereIsIt_DataMutex");
    
    // 640 MB for ~20 million records
    size_t recordsSize = sizeof(std::atomic<uint32_t>) + 20000000ULL * sizeof(FileRecord);
    m_hRecordsMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, GetPermissiveSA(), PAGE_READWRITE, 
        (DWORD)(recordsSize >> 32), (DWORD)(recordsSize & 0xFFFFFFFF), L"Global\\WhereIsIt_Records");
    
    if (m_hRecordsMapping) {
        uint8_t* base = (uint8_t*)MapViewOfFile(m_hRecordsMapping, FILE_MAP_WRITE, 0, 0, recordsSize);
        if (base) {
            m_recordsCount = (std::atomic<uint32_t>*)base;
            m_recordsShared = (FileRecord*)(base + sizeof(std::atomic<uint32_t>));
            // Only initialize count to 0 if we actually created it (not just opened it)
            if (GetLastError() != ERROR_ALREADY_EXISTS) {
                m_recordsCount->store(0, std::memory_order_relaxed);
            }
        }
    }
    
    m_hDrivesMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, GetPermissiveSA(), PAGE_READWRITE, 
        0, 64 * 4 * sizeof(wchar_t), L"Global\\WhereIsIt_Drives");
    if (m_hDrivesMapping) {
        m_driveLettersShared = (wchar_t(*)[4])MapViewOfFile(m_hDrivesMapping, FILE_MAP_WRITE, 0, 0, 64 * 4 * sizeof(wchar_t));
        if (m_driveLettersShared && GetLastError() != ERROR_ALREADY_EXISTS) {
            memset(m_driveLettersShared, 0, 64 * 4 * sizeof(wchar_t));
        }
    }
    
    m_currentResults = std::make_shared<std::vector<uint32_t>>();
}

IndexingEngine::~IndexingEngine() { Stop(); }

void IndexingEngine::Start() {
    if (m_running) return;
    // Create a manual-reset event used to wake MonitorChanges immediately on Stop().
    m_stopEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
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
    // Signal m_stopEvent so MonitorChanges wakes from WaitForMultipleObjects immediately
    // rather than blocking up to the next timeout.
    if (m_stopEvent) SetEvent(m_stopEvent);
    m_searchEvent.notify_all();
    if (m_mainWorker.joinable()) m_mainWorker.join();
    if (m_searchWorker.joinable()) m_searchWorker.join();
    if (m_stopEvent) { CloseHandle(m_stopEvent); m_stopEvent = NULL; }
    CloseAllDriveHandles();
}

std::shared_ptr<std::vector<uint32_t>> IndexingEngine::GetSearchResults() {
    std::unique_lock<std::mutex> lock(m_resultBufferMutex);
    return m_currentResults;
}

std::shared_ptr<std::vector<uint32_t>> IndexingEngine::WaitForNewResults(const std::shared_ptr<std::vector<uint32_t>>& prev, int timeoutMs) {
    std::unique_lock<std::mutex> lock(m_resultBufferMutex);
    m_resultCv.wait_for(lock, std::chrono::milliseconds(timeoutMs), [this, &prev] {
        return m_currentResults != prev;
    });
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
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    std::wstring tmpPath = filePath + L".tmp";

    struct IndexHeader  { uint32_t Magic, Version, DriveCount, RecordCount, PoolSize, GiantCount; };
    struct DriveInfoBin { wchar_t Letter[4]; uint32_t Serial; uint64_t LastUsn; };

    const uint32_t driveCount  = (uint32_t)m_drives.size();
    const uint32_t recordCount = GetRecordCount();
    const uint32_t poolSize    = (uint32_t)m_pool.GetSize();
    const uint32_t giantCount  = (uint32_t)m_giantFileSizes.size();

    const uint64_t fileSize =
        sizeof(IndexHeader) +
        (uint64_t)driveCount  * sizeof(DriveInfoBin) +
        (uint64_t)recordCount * sizeof(FileRecord) +
        poolSize +
        (uint64_t)giantCount  * (sizeof(uint32_t) + sizeof(uint64_t));

    struct Handle {
        HANDLE h = INVALID_HANDLE_VALUE;
        Handle() = default;
        explicit Handle(HANDLE h_) : h(h_) {}
        ~Handle() { if (h != INVALID_HANDLE_VALUE && h != NULL) CloseHandle(h); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
    };

    Handle hFile(CreateFileW(tmpPath.c_str(), GENERIC_READ | GENERIC_WRITE, 0, NULL,
                             CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL));
    if (hFile.h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER li; li.QuadPart = (LONGLONG)fileSize;
    if (!SetFilePointerEx(hFile.h, li, NULL, FILE_BEGIN) || !SetEndOfFile(hFile.h)) {
        DeleteFileW(tmpPath.c_str()); return false;
    }

    Handle hMap(CreateFileMappingW(hFile.h, NULL, PAGE_READWRITE, 0, 0, NULL));
    if (!hMap.h) { DeleteFileW(tmpPath.c_str()); return false; }

    uint8_t* base = (uint8_t*)MapViewOfFile(hMap.h, FILE_MAP_WRITE, 0, 0, 0);
    if (!base) { DeleteFileW(tmpPath.c_str()); return false; }

    uint8_t* p = base;

    IndexHeader hdr = { 0x54494957, 8, driveCount, recordCount, poolSize, giantCount };
    memcpy(p, &hdr, sizeof(hdr)); p += sizeof(hdr);

    for (const auto& d : m_drives) {
        DriveInfoBin di = {};
        wcscpy_s(di.Letter, d.Letter.c_str());
        di.Serial  = d.SerialNumber;
        di.LastUsn = d.LastProcessedUsn;
        memcpy(p, &di, sizeof(di)); p += sizeof(di);
    }

    memcpy(p, m_recordsShared, (size_t)recordCount * sizeof(FileRecord));
    p += (size_t)recordCount * sizeof(FileRecord);

    m_pool.WriteRawTo(p);
    p += poolSize;

    for (const auto& kv : m_giantFileSizes) {
        memcpy(p, &kv.first,  sizeof(uint32_t)); p += sizeof(uint32_t);
        memcpy(p, &kv.second, sizeof(uint64_t)); p += sizeof(uint64_t);
    }

    FlushViewOfFile(base, 0);
    UnmapViewOfFile(base);
    CloseHandle(hMap.h);  hMap.h  = NULL;
    CloseHandle(hFile.h); hFile.h = INVALID_HANDLE_VALUE;

    if (!MoveFileExW(tmpPath.c_str(), filePath.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(tmpPath.c_str()); return false;
    }
    return true;
}

bool IndexingEngine::LoadIndex(const std::wstring& filePath) {
    auto clearState = [this]() {
        if (m_recordsCount) m_recordsCount->store(0, std::memory_order_release);
        m_mftLookupTables.clear(); m_pool.Clear(); m_giantFileSizes.clear();
        std::lock_guard<std::mutex> cacheLock(m_nameCacheMutex);
        m_nameCache.clear();
        m_nameCacheLru.clear();
    };

    struct Handle {
        HANDLE h = INVALID_HANDLE_VALUE;
        Handle() = default;
        explicit Handle(HANDLE h_) : h(h_) {}
        ~Handle() { if (h != INVALID_HANDLE_VALUE && h != NULL) CloseHandle(h); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
    };

    Handle hFile(CreateFileW(filePath.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                             OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL));
    if (hFile.h == INVALID_HANDLE_VALUE) {
        Logger::Log(L"[WhereIsIt] index.dat not found or could not be opened.");
        return false;
    }

    LARGE_INTEGER fileSize = {};
    if (!GetFileSizeEx(hFile.h, &fileSize) || fileSize.QuadPart < (LONGLONG)(sizeof(uint32_t) * 6)) {
        Logger::Log(L"[WhereIsIt] index.dat too small.");
        return false;
    }

    Handle hMap(CreateFileMappingW(hFile.h, NULL, PAGE_READONLY, 0, 0, NULL));
    if (!hMap.h) {
        Logger::Log(L"[WhereIsIt] Failed to create file mapping.");
        return false;
    }

    const uint8_t* base = (const uint8_t*)MapViewOfFile(hMap.h, FILE_MAP_READ, 0, 0, 0);
    if (!base) {
        Logger::Log(L"[WhereIsIt] Failed to map index view.");
        return false;
    }
    struct ViewGuard { const uint8_t* p; ~ViewGuard() { if (p) UnmapViewOfFile(p); } } vg{ base };

    const uint8_t* const end = base + (size_t)fileSize.QuadPart;
    const uint8_t* p = base;

    struct IndexHeader  { uint32_t Magic, Version, DriveCount, RecordCount, PoolSize, GiantCount; };
    struct DriveInfoBin { wchar_t Letter[4]; uint32_t Serial; uint64_t LastUsn; };

    if (p + sizeof(IndexHeader) > end) { Logger::Log(L"[WhereIsIt] Index too small for header."); return false; }
    const IndexHeader& h = *(const IndexHeader*)p; p += sizeof(IndexHeader);

    if (h.Magic != 0x54494957 || h.Version != 8) {
        Logger::Log(L"[WhereIsIt] Index magic/version mismatch.");
        return false;
    }
    if (h.DriveCount == 0 || h.DriveCount > 64 ||
        h.RecordCount > 100000000 ||
        h.PoolSize == 0 || h.PoolSize > (512u * 1024u * 1024u) ||
        h.GiantCount > h.RecordCount) {
        Logger::Log(L"[WhereIsIt] Index header contains invalid values.");
        return false;
    }
    if (h.DriveCount != (uint32_t)m_drives.size()) {
        Logger::Log(L"[WhereIsIt] Drive count mismatch. Need full scan.");
        return false;
    }

    // --- Drive table ---
    if (p + (size_t)h.DriveCount * sizeof(DriveInfoBin) > end) { clearState(); return false; }
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        const DriveInfoBin& di = *(const DriveInfoBin*)p; p += sizeof(DriveInfoBin);
        if (m_drives[i].SerialNumber != di.Serial || wcscmp(m_drives[i].Letter.c_str(), di.Letter) != 0) {
            Logger::Log(L"[WhereIsIt] Drive configuration changed. Need full scan.");
            clearState(); return false;
        }
        m_drives[i].LastProcessedUsn = di.LastUsn;
    }

    // --- File records — single memcpy from mapped view ---
    const size_t recBytes = (size_t)h.RecordCount * sizeof(FileRecord);
    if (p + recBytes > end) { Logger::Log(L"[WhereIsIt] Index truncated at records."); clearState(); return false; }
    std::vector<FileRecord> tempRecords;
    try { tempRecords.resize(h.RecordCount); } catch (...) { clearState(); return false; }
    memcpy(tempRecords.data(), p, recBytes); p += recBytes;

    // --- String pool — single LoadRawData into arena ---
    if (p + h.PoolSize > end) { Logger::Log(L"[WhereIsIt] Index truncated at pool."); clearState(); return false; }
    m_pool.Clear();
    m_pool.LoadRawData((const char*)p, h.PoolSize); p += h.PoolSize;

    // --- Giant-file size entries ---
    std::unordered_map<uint32_t, uint64_t> tempGiant;
    const size_t giantEntry = sizeof(uint32_t) + sizeof(uint64_t);
    if (p + (size_t)h.GiantCount * giantEntry > end) { clearState(); return false; }
    for (uint32_t gi = 0; gi < h.GiantCount; ++gi) {
        uint32_t idx;  memcpy(&idx,  p, sizeof(uint32_t)); p += sizeof(uint32_t);
        uint64_t sz;   memcpy(&sz,   p, sizeof(uint64_t)); p += sizeof(uint64_t);
        tempGiant[idx] = sz;
    }

    // --- Rebuild MFT lookup tables ---
    std::vector<std::vector<uint32_t>> tempLookup(h.DriveCount);
    std::vector<uint32_t> maxMft(h.DriveCount, 0);
    for (const auto& rec : tempRecords) {
        if (rec.DriveIndex >= h.DriveCount || rec.NamePoolOffset >= h.PoolSize) { clearState(); return false; }
        if (rec.MftIndex != 0xFFFFFFFF && rec.MftIndex > maxMft[rec.DriveIndex])
            maxMft[rec.DriveIndex] = rec.MftIndex;
    }
    for (uint32_t i = 0; i < h.DriveCount; ++i) {
        try { tempLookup[i].assign((size_t)maxMft[i] + 1, 0xFFFFFFFF); }
        catch (...) { Logger::Log(L"[WhereIsIt] MFT lookup allocation failed."); clearState(); return false; }
    }
    for (uint32_t i = 0; i < (uint32_t)tempRecords.size(); ++i) {
        const auto& rec = tempRecords[i];
        if (rec.MftIndex != 0xFFFFFFFF)
            tempLookup[rec.DriveIndex][rec.MftIndex] = i;
    }

    // --- Commit under exclusive write lock ---
    std::unique_lock<std::shared_mutex> dataLock(m_dataMutex);
    if (m_recordsCount) m_recordsCount->store(0, std::memory_order_relaxed);
    if (m_recordsShared && tempRecords.size() <= 20000000ULL) {
        memcpy(m_recordsShared, tempRecords.data(), tempRecords.size() * sizeof(FileRecord));
        if (m_recordsCount) m_recordsCount->store((uint32_t)tempRecords.size(), std::memory_order_release);
    }
    m_mftLookupTables = std::move(tempLookup);
    m_giantFileSizes  = std::move(tempGiant);
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
    if (recordIdx < GetRecordCount()) return m_recordsShared[recordIdx];
    return {};
}

uint64_t IndexingEngine::ResolveFileSize(const FileRecord& rec, uint32_t recordIndex) const {
    if (!rec.IsGiantFile) return rec.FileSize;
    auto it = m_giantFileSizes.find(recordIndex);
    if (it != m_giantFileSizes.end()) return it->second;
    return 0xFFFFFFFFULL;
}

uint32_t IndexingEngine::FileTimeToEpoch(uint64_t fileTime) const {
    return FileTimeToUnixEpochSeconds(fileTime);
}

uint64_t IndexingEngine::EpochToFileTime(uint32_t epoch) const {
    return UnixEpochSecondsToFileTime(epoch);
}

uint64_t IndexingEngine::GetRecordFileSize(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    if (recordIdx >= GetRecordCount()) return 0;
    return ResolveFileSize(m_recordsShared[recordIdx], recordIdx);
}

uint64_t IndexingEngine::GetRecordLastModifiedFileTime(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    if (recordIdx >= GetRecordCount()) return 0;
    return EpochToFileTime(m_recordsShared[recordIdx].LastModifiedEpoch);
}

std::wstring IndexingEngine::GetRecordName(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    if (recordIdx >= GetRecordCount()) return L"";
    return GetWideNameFromPoolOffsetCached(m_recordsShared[recordIdx].NamePoolOffset);
}

// Single shared_lock acquisition returning both record and wide name — use in paint hot paths.
std::pair<FileRecord, std::wstring> IndexingEngine::GetRecordAndName(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    if (recordIdx >= GetRecordCount()) return { {}, L"" };
    const FileRecord& rec = m_recordsShared[recordIdx];
    return { rec, GetWideNameFromPoolOffsetCached(rec.NamePoolOffset) };
}

std::wstring IndexingEngine::GetWideNameFromPoolOffsetCached(uint32_t namePoolOffset) const {
    {
        std::lock_guard<std::mutex> cacheLock(m_nameCacheMutex);
        auto it = m_nameCache.find(namePoolOffset);
        if (it != m_nameCache.end()) {
            m_nameCacheLru.splice(m_nameCacheLru.begin(), m_nameCacheLru, it->second.second);
            return it->second.first;
        }
    }

    const char* nameA = m_pool.GetString(namePoolOffset);
    if (!nameA || !nameA[0]) return L"";
    int sz = MultiByteToWideChar(CP_UTF8, 0, nameA, -1, NULL, 0);
    if (sz <= 1) return L"";
    std::wstring converted(sz - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, nameA, -1, &converted[0], sz);

    {
        std::lock_guard<std::mutex> cacheLock(m_nameCacheMutex);
        auto it = m_nameCache.find(namePoolOffset);
        if (it != m_nameCache.end()) {
            m_nameCacheLru.splice(m_nameCacheLru.begin(), m_nameCacheLru, it->second.second);
            return it->second.first;
        }
        m_nameCacheLru.push_front(namePoolOffset);
        m_nameCache[namePoolOffset] = { converted, m_nameCacheLru.begin() };
        if (m_nameCache.size() > kWideNameCacheCapacity) {
            uint32_t evict = m_nameCacheLru.back();
            m_nameCacheLru.pop_back();
            m_nameCache.erase(evict);
        }
    }
    return converted;
}

std::wstring IndexingEngine::GetFullPath(uint32_t recordIdx) const {
    std::shared_lock<std::shared_mutex> lock(m_dataMutex);
    return GetFullPathInternal(recordIdx);
}

std::wstring IndexingEngine::GetFullPathInternal(uint32_t recordIdx) const {
    if (recordIdx >= (uint32_t)GetRecordCount()) return L"";
    
    thread_local const char* parts[4096];
    int partsCount = 0;
    thread_local uint32_t visited[4096];
    int visitedCount = 0;
    
    uint32_t cur = recordIdx;
    uint8_t di = m_recordsShared[recordIdx].DriveIndex;
    if (di >= (uint8_t)m_drives.size() || di >= (uint8_t)m_mftLookupTables.size()) return L"";

    while (partsCount < 4096 && visitedCount < 4096) {
        bool cycle = false;
        for (int i = 0; i < visitedCount; i++) {
            if (visited[i] == cur) { cycle = true; break; }
        }
        if (cycle) break;
        visited[visitedCount++] = cur;

        const FileRecord& r = m_recordsShared[cur]; 
        const char* name = m_pool.GetString(r.NamePoolOffset);
        if (name[0] != '.' || name[1] != '\0') {
            parts[partsCount++] = name;
        }
        if (r.ParentMftIndex < 5 || r.ParentMftIndex >= (uint32_t)m_mftLookupTables[di].size()) break;
        uint32_t pi = m_mftLookupTables[di][r.ParentMftIndex];
        if (pi == 0xFFFFFFFF || pi >= (uint32_t)GetRecordCount() || m_recordsShared[pi].MftSequence != r.ParentSequence) break;
        if (pi == cur) break; cur = pi;
    }
    
    std::string pa;
    for (int i = partsCount - 1; i >= 0; --i) {
        if (!pa.empty()) pa += "\\";
        pa += parts[i];
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
    if (recordIdx >= (uint32_t)GetRecordCount()) return L"";
    const FileRecord& child = m_recordsShared[recordIdx]; uint8_t di = child.DriveIndex;
    if (di >= (uint8_t)m_drives.size() || di >= (uint8_t)m_mftLookupTables.size()) return L"";
    if (child.ParentMftIndex < 5 || child.ParentMftIndex >= (uint32_t)m_mftLookupTables[di].size()) return m_drives[di].Letter;
    uint32_t pi = m_mftLookupTables[di][child.ParentMftIndex];
    if (pi == 0xFFFFFFFF || pi >= (uint32_t)GetRecordCount() || m_recordsShared[pi].MftSequence != child.ParentSequence) return m_drives[di].Letter;
    return GetFullPathInternal(pi);
}

void IndexingEngine::UpdatePreSortedIndex() {
    SetStatus(L"Sorting index...");
    std::vector<uint32_t> sorted;

    // Phase 1: collect indices and snapshot the name pointers under a short shared lock.
    // Sorting itself does NOT hold any lock — the sort comparator uses snapshots.
    struct NameEntry { uint32_t idx; const char* name; };
    std::vector<NameEntry> entries;
    {
        std::shared_lock<std::shared_mutex> dataLock(m_dataMutex);
        entries.reserve(GetRecordCount());
        for (uint32_t i = 0; i < (uint32_t)GetRecordCount(); ++i) {
            if (m_recordsShared[i].MftIndex != 0xFFFFFFFF)
                entries.push_back({ i, m_pool.GetString(m_recordsShared[i].NamePoolOffset) });
        }
    } // shared lock released before sort

    // Phase 2: sort without any lock held.
    std::sort(entries.begin(), entries.end(), [](const NameEntry& a, const NameEntry& b) {
        int cmp = FastCompareIgnoreCase(a.name, b.name);
        return cmp != 0 ? (cmp < 0) : (a.idx < b.idx);
    });

    sorted.resize(entries.size());
    for (size_t i = 0; i < entries.size(); ++i) sorted[i] = entries[i].idx;

    // Phase 3: write under exclusive lock.
    {
        std::unique_lock<std::shared_mutex> lockWrite(m_dataMutex);
        m_preSortedByName = std::move(sorted);
    }
}

void IndexingEngine::Search(const std::string& q) {
    { 
        std::lock_guard<std::mutex> lock(m_searchSyncMutex); 
        m_pendingSearchQuery = q; 
        m_isSearchRequested = true; 
    }
    m_searchEvent.notify_one();
}

void IndexingEngine::Sort(QuerySortKey key, bool descending) {
    {
        std::lock_guard<std::mutex> lock(m_searchSyncMutex);
        m_currentSortKey = key;
        m_currentSortDescending = descending;
        m_isSortOnlyRequested = true;
    }
    m_searchEvent.notify_one();
}

namespace {
    class SortAbortedException : public std::exception {};

    uint32_t ResolveParentRecordIndex(
        const FileRecord& rec,
        const std::vector<FileRecord>& records,
        const std::vector<std::vector<uint32_t>>& mftLookupTables) {
        const uint8_t di = rec.DriveIndex;
        if (di >= (uint8_t)mftLookupTables.size()) return 0xFFFFFFFF;
        if (rec.ParentMftIndex < 5 || rec.ParentMftIndex >= (uint32_t)mftLookupTables[di].size()) return 0xFFFFFFFF;
        const uint32_t parentIdx = mftLookupTables[di][rec.ParentMftIndex];
        if (parentIdx == 0xFFFFFFFF || parentIdx >= (uint32_t)records.size()) return 0xFFFFFFFF;
        if (records[parentIdx].MftSequence != rec.ParentSequence) return 0xFFFFFFFF;
        return parentIdx;
    }

    int ComparePathByHierarchy(
        uint32_t aIdx,
        uint32_t bIdx,
        const std::vector<FileRecord>& records,
        const std::vector<std::string>& driveLetters,
        const std::vector<std::vector<uint32_t>>& mftLookupTables,
        const StringPool& pool) {
        if (aIdx >= records.size() || bIdx >= records.size()) return (aIdx < bIdx) ? -1 : 1;

        const FileRecord& aRec = records[aIdx];
        const FileRecord& bRec = records[bIdx];

        if (aRec.DriveIndex != bRec.DriveIndex) {
            const char* aDrive = (aRec.DriveIndex < driveLetters.size()) ? driveLetters[aRec.DriveIndex].c_str() : "";
            const char* bDrive = (bRec.DriveIndex < driveLetters.size()) ? driveLetters[bRec.DriveIndex].c_str() : "";
            int driveCmp = FastCompareIgnoreCase(aDrive, bDrive);
            if (driveCmp != 0) return driveCmp;
            return (aRec.DriveIndex < bRec.DriveIndex) ? -1 : 1;
        }

        constexpr size_t kMaxDepth = 1024;
        uint32_t aChain[kMaxDepth];
        uint32_t bChain[kMaxDepth];
        size_t aCount = 0, bCount = 0;

        uint32_t cur = aIdx;
        while (aCount < kMaxDepth && cur != 0xFFFFFFFF) {
            aChain[aCount++] = cur;
            const uint32_t parent = ResolveParentRecordIndex(records[cur], records, mftLookupTables);
            if (parent == cur) break;
            cur = parent;
        }
        cur = bIdx;
        while (bCount < kMaxDepth && cur != 0xFFFFFFFF) {
            bChain[bCount++] = cur;
            const uint32_t parent = ResolveParentRecordIndex(records[cur], records, mftLookupTables);
            if (parent == cur) break;
            cur = parent;
        }

        size_t ai = aCount;
        size_t bi = bCount;
        while (ai > 0 && bi > 0) {
            const char* aName = pool.GetString(records[aChain[ai - 1]].NamePoolOffset);
            const char* bName = pool.GetString(records[bChain[bi - 1]].NamePoolOffset);
            int cmp = FastCompareIgnoreCase(aName ? aName : "", bName ? bName : "");
            if (cmp != 0) return cmp;
            --ai;
            --bi;
        }
        if (ai == 0 && bi == 0) return 0;
        return (ai == 0) ? -1 : 1;
    }
}

void IndexingEngine::SearchThread() {
    std::function<bool(const QueryNode*, const QueryPlan&, uint32_t, const FileRecord&, const std::string&, const std::function<std::string()>&)> evaluateTerm;
    evaluateTerm = [this, &evaluateTerm](const QueryNode* node, const QueryPlan& plan, uint32_t recIdx, const FileRecord& rec, const std::string& name, const std::function<std::string()>& getFullPath) -> bool {
        if (!node) return true;
        if (node->Type == QueryNodeType::And) return evaluateTerm(node->Left.get(), plan, recIdx, rec, name, getFullPath) && evaluateTerm(node->Right.get(), plan, recIdx, rec, name, getFullPath);
        if (node->Type == QueryNodeType::Or) return evaluateTerm(node->Left.get(), plan, recIdx, rec, name, getFullPath) || evaluateTerm(node->Right.get(), plan, recIdx, rec, name, getFullPath);
        if (node->Type == QueryNodeType::Not) return !evaluateTerm(node->Left.get(), plan, recIdx, rec, name, getFullPath);

        const std::string& term = node->Term;
        if (term.empty()) return true;
        // TermLower precomputed at parse time — zero allocation per record.
        const std::string& low = node->TermLower;

        if (low.rfind("ext:", 0) == 0) {
            // Zero-allocation: use raw pointer into TermLower and scan name directly.
            const char* extNeedle = node->TermLower.c_str() + 4;
            const char* dot = strrchr(name.c_str(), '.');
            return dot && *(dot + 1) && (FastCompareIgnoreCase(dot + 1, extNeedle) == 0);
        }
        if (low.rfind("path:", 0) == 0) return FastContains(getFullPath(), term.substr(5), false);
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
            const uint64_t sizeValue = ResolveFileSize(rec, recIdx);
            if (cmp == 2) return sizeValue >= value;
            if (cmp == -2) return sizeValue <= value;
            if (cmp == 1) return sizeValue > value;
            if (cmp == -1) return sizeValue < value;
            return sizeValue == value;
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
            const uint32_t queryEpoch = FileTimeToEpoch(value);
            if (cmp == 2) return rec.LastModifiedEpoch >= queryEpoch;
            if (cmp == -2) return rec.LastModifiedEpoch <= queryEpoch;
            if (cmp == 1) return rec.LastModifiedEpoch > queryEpoch;
            if (cmp == -1) return rec.LastModifiedEpoch < queryEpoch;
            return rec.LastModifiedEpoch == queryEpoch;
        }

        if (plan.Config.RegexMode && !plan.CompiledRegex.empty()) {
            for (const auto& rx : plan.CompiledRegex) {
                if (rx.match(name)) return true;
                if (rx.match(getFullPath())) return true;
            }
            return false;
        }

        // DEFAULT SEARCH WITH WILDCARDS AND PATHS
        size_t lastSlash = term.find_last_of("\\/");
        if (lastSlash != std::string::npos) {
            // Path-based search
            std::string dirPart = term.substr(0, lastSlash + 1);
            std::string filePart = term.substr(lastSlash + 1);

            if (!filePart.empty()) {
                bool matchName = false;
                if (filePart.find_first_of("*?") != std::string::npos) {
                    matchName = WildcardMatchIAscii(filePart.c_str(), name.c_str());
                } else {
                    // Exact filename match: C:\Windows\dd.exe must only match "dd.exe",
                    // not substrings like "odd.exe". Wildcard form *dd.exe is handled above.
                    if (plan.Config.CaseSensitive)
                        matchName = (name == filePart);
                    else
                        matchName = (FastCompareIgnoreCase(name.c_str(), filePart.c_str()) == 0);
                }
                if (!matchName) return false;
            }

            std::string p = getFullPath();
            if (!p.empty() && !FastContains(p, dirPart, plan.Config.CaseSensitive)) return false;
            return true;
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
            // Sort-only: no logging in hot path.
        } else {
            // Logging removed from normal search path to avoid synchronous OutputDebugStringW overhead per keystroke.

            QueryPlan plan = BuildQueryPlan(query);
            if (!plan.Success) {
                SetStatus(L"Query Error: " + plan.ErrorMessage);
                std::lock_guard<std::mutex> lock(m_resultBufferMutex);
                m_currentResults = results; // Empty results
                continue;
            }

            if (query.find("sort:") != std::string::npos || query.find("desc") != std::string::npos || query.find("asc") != std::string::npos) {
                sortKey = plan.Config.SortKey;
                sortDescending = plan.Config.SortDescending;
            }

            {
                std::shared_lock<std::shared_mutex> dataLock(m_dataMutex);
                results->reserve(GetRecordCount());
                bool usePreSorted = (sortKey == QuerySortKey::Name && !sortDescending && !m_preSortedByName.empty());

                if (query.empty()) {
                    if (usePreSorted) {
                        for (size_t i = 0; i < m_preSortedByName.size(); ++i) {
                            if ((i & 0x3F) == 0 && m_isSearchRequested.load(std::memory_order_relaxed)) break;
                            uint32_t idx = m_preSortedByName[i];
                            if (m_recordsShared[idx].MftIndex != 0xFFFFFFFF) results->push_back(idx);
                        }
                    } else {
                        for (uint32_t i = 0; i < (uint32_t)GetRecordCount(); ++i) {
                            if ((i & 0x3F) == 0 && m_isSearchRequested.load(std::memory_order_relaxed)) break;
                            if (m_recordsShared[i].MftIndex != 0xFFFFFFFF) results->push_back(i);
                        }
                    }
                } else {
                    // --- FAST PATH: simple case-insensitive substring query ---
                    // Detects single-term queries with no operators, colons, wildcards, or spaces.
                    // Avoids ALL heap allocations per record: no std::string, no std::function.
                    // Covers ~95% of real-world usage patterns (e.g. typing "1", "foo", "report").
                    auto isSimpleQuery = [&]() -> bool {
                        if (query.empty()) return false;
                        for (char c : query) {
                            if (c == ' ' || c == '\t' || c == ':' || c == '*' || c == '?' ||
                                c == '(' || c == ')' || c == '"') return false;
                        }
                        // No operators as standalone tokens
                        std::string qLow = ToLowerAscii(query);
                        return qLow != "and" && qLow != "or" && qLow != "not";
                    };

                    // Precompute lowercase needle once (not per-record)
                    const std::string needleLow = ToLowerAscii(query);
                    const char* needlePtr = needleLow.c_str();
                    const size_t needleLen = needleLow.size();

                    if (isSimpleQuery()) {
                        // FAST PATH: tight loop, zero allocation per record.
                        auto fastLoop = [&](uint32_t count, auto getIndex) {
                            for (uint32_t i = 0; i < count; ++i) {
                                // Check for new search request every 64 records.
                                if ((i & 0x3F) == 0) {
                                    if (m_isSearchRequested.load(std::memory_order_relaxed)) return;
                                }
                                uint32_t idx = getIndex(i);
                                const auto& rec = m_recordsShared[idx];
                                if (rec.MftIndex == 0xFFFFFFFF) continue;
                                // Zero allocation: use const char* directly from pool.
                                const char* name = m_pool.GetString(rec.NamePoolOffset);
                                if (FastContainsCIPtr(name, needlePtr, needleLen))
                                    results->push_back(idx);
                            }
                        };
                        auto parallelFastLoop = [&](uint32_t count, auto getIndex) {
                            const unsigned int hw = std::thread::hardware_concurrency();
                            const uint32_t workerCount = (std::min)((uint32_t)(hw ? hw : 4), (uint32_t)8);
                            if (workerCount < 2 || count < 200000) {
                                fastLoop(count, getIndex);
                                return;
                            }

                            std::vector<std::vector<uint32_t>> chunks(workerCount);
                            std::vector<std::thread> workers;
                            workers.reserve(workerCount > 0 ? workerCount - 1 : 0);

                            auto workerFn = [&](uint32_t begin, uint32_t end, uint32_t workerId) {
                                auto& local = chunks[workerId];
                                local.reserve((end - begin) / 8 + 64);
                                for (uint32_t i = begin; i < end; ++i) {
                                    if ((i & 0xFF) == 0 && m_isSearchRequested.load(std::memory_order_relaxed)) return;
                                    const uint32_t idx = getIndex(i);
                                    const auto& rec = m_recordsShared[idx];
                                    if (rec.MftIndex == 0xFFFFFFFF) continue;
                                    const char* name = m_pool.GetString(rec.NamePoolOffset);
                                    if (FastContainsCIPtr(name, needlePtr, needleLen)) local.push_back(idx);
                                }
                            };

                            const uint32_t base = count / workerCount;
                            const uint32_t rem = count % workerCount;
                            uint32_t begin = 0;
                            for (uint32_t w = 0; w < workerCount; ++w) {
                                const uint32_t len = base + (w < rem ? 1 : 0);
                                const uint32_t end = begin + len;
                                if (w == 0) workerFn(begin, end, w);
                                else workers.emplace_back(workerFn, begin, end, w);
                                begin = end;
                            }
                            for (auto& t : workers) t.join();
                            if (m_isSearchRequested.load(std::memory_order_relaxed)) return;

                            size_t total = 0;
                            for (const auto& c : chunks) total += c.size();
                            results->clear();
                            results->reserve(total);
                            for (auto& c : chunks) {
                                results->insert(results->end(), c.begin(), c.end());
                            }
                        };
                        if (usePreSorted)
                            parallelFastLoop((uint32_t)m_preSortedByName.size(), [&](uint32_t i) { return m_preSortedByName[i]; });
                        else
                            parallelFastLoop((uint32_t)GetRecordCount(), [&](uint32_t i) { return i; });
                    } else {
                        // MEDIUM FAST PATH: single-term path query (e.g. C:\Windows\*.exe).
                        // Detects queries that contain a slash but no operators, spaces, quotes,
                        // or operator-colons (drive-letter colon at position 1 is allowed).
                        // Checks filename via raw pool pointer (zero allocation); builds full
                        // path lazily only for records whose filename already matched.
                        auto isPathQuery = [&]() -> bool {
                            if (plan.Root == nullptr || plan.Root->Type != QueryNodeType::Term) return false;
                            if (plan.Config.RegexMode || plan.Config.WholeWord) return false;
                            bool hasSlash = false;
                            for (size_t ci = 0; ci < query.size(); ++ci) {
                                char c = query[ci];
                                if (c == ' ' || c == '\t' || c == '(' || c == ')' || c == '"') return false;
                                if (c == '\\' || c == '/') { hasSlash = true; continue; }
                                // Allow colon only as drive-letter separator (position 1)
                                if (c == ':' && ci != 1) return false;
                            }
                            return hasSlash;
                        };

                        if (isPathQuery()) {
                            const std::string& term = query;
                            size_t lastSlash = term.find_last_of("\\/");
                            std::string dirPart  = term.substr(0, lastSlash + 1);
                            std::string filePart = term.substr(lastSlash + 1);
                            bool fileHasWildcard = !filePart.empty() && filePart.find_first_of("*?") != std::string::npos;
                            bool caseSensitive   = plan.Config.CaseSensitive;

                            auto pathLoop = [&](uint32_t count, auto getIndex) {
                                for (uint32_t i = 0; i < count; ++i) {
                                    if ((i & 0x3F) == 0) {
                                        if (m_isSearchRequested.load(std::memory_order_relaxed)) return;
                                    }
                                    uint32_t idx = getIndex(i);
                                    const auto& rec = m_recordsShared[idx];
                                    if (rec.MftIndex == 0xFFFFFFFF) continue;

                                    const char* name = m_pool.GetString(rec.NamePoolOffset);

                                    // 1. Check filename (no allocation, no full-path construction)
                                    if (!filePart.empty()) {
                                        bool matchName = false;
                                        if (fileHasWildcard) {
                                            matchName = WildcardMatchIAscii(filePart.c_str(), name);
                                        } else {
                                            matchName = caseSensitive
                                                ? (strcmp(name, filePart.c_str()) == 0)
                                                : (FastCompareIgnoreCase(name, filePart.c_str()) == 0);
                                        }
                                        if (!matchName) continue;
                                    }

                                    // 2. Filename matched — now check dir (lazy, only for hits)
                                    if (!dirPart.empty()) {
                                        std::string fullPath = WideToUtf8(GetFullPathInternal(idx));
                                        if (!FastContains(fullPath, dirPart, caseSensitive)) continue;
                                    }

                                    results->push_back(idx);
                                }
                            };
                            if (usePreSorted)
                                pathLoop((uint32_t)m_preSortedByName.size(), [&](uint32_t i) { return m_preSortedByName[i]; });
                            else
                                pathLoop((uint32_t)GetRecordCount(), [&](uint32_t i) { return i; });
                        } else {
                            // FULL PATH: complex query with operators/modifiers — uses evaluateTerm.
                            auto evalLoop = [&](uint32_t count, auto getIndex) {
                                for (uint32_t i = 0; i < count; ++i) {
                                    if ((i & 0x3F) == 0) {
                                        if (m_isSearchRequested.load(std::memory_order_relaxed)) return;
                                    }
                                    uint32_t idx = getIndex(i);
                                    const auto& rec = m_recordsShared[idx];
                                    if (rec.MftIndex == 0xFFFFFFFF) continue;
                                    const std::string nameStr(m_pool.GetString(rec.NamePoolOffset));
                                    std::string cachedPath;
                                    bool pathCached = false;
                                    auto getPath = [&]() -> std::string {
                                        if (!pathCached) { cachedPath = WideToUtf8(GetFullPathInternal(idx)); pathCached = true; }
                                        return cachedPath;
                                    };
                                    if (evaluateTerm(plan.Root.get(), plan, idx, rec, nameStr, getPath))
                                        results->push_back(idx);
                                }
                            };
                            if (usePreSorted)
                                evalLoop((uint32_t)m_preSortedByName.size(), [&](uint32_t i) { return m_preSortedByName[i]; });
                            else
                                evalLoop((uint32_t)GetRecordCount(), [&](uint32_t i) { return i; });
                        }
                    }
                }
            }
        }

        if (m_isSearchRequested) continue;

        {
            std::shared_lock<std::shared_mutex> dataLock(m_dataMutex);
            
            bool usePreSorted = (sortKey == QuerySortKey::Name && !sortDescending && !m_preSortedByName.empty());
            
            if (usePreSorted) {
                // Already sorted!
            } else if (sortKey == QuerySortKey::Path) {
                // O(N) pre-pass: resolve and cache full paths once, then O(N log N) sort on
                // plain strings.  Avoids the O(N log N × depth) MFT ancestor-chain traversal
                // that ComparePathByHierarchy performs on every comparator call.
                const size_t n = results->size();
                struct PathEntry { std::string path; uint32_t recIdx; };
                std::vector<PathEntry> pathEntries;
                pathEntries.reserve(n);
                bool aborted = false;
                for (size_t pi = 0; pi < n; ++pi) {
                    if ((pi & 0xFF) == 0 && m_isSearchRequested.load(std::memory_order_relaxed)) { aborted = true; break; }
                    pathEntries.push_back({WideToUtf8(GetFullPathInternal((*results)[pi])), (*results)[pi]});
                }
                if (!aborted) {
                    try {
                        int cmpCount = 0;
                        std::sort(pathEntries.begin(), pathEntries.end(),
                            [sortDescending, &cmpCount, this](const PathEntry& a, const PathEntry& b) {
                                if ((++cmpCount & 0x3F) == 0 && m_isSearchRequested.load(std::memory_order_relaxed)) throw SortAbortedException();
                                int cmp = FastCompareIgnoreCase(a.path.c_str(), b.path.c_str());
                                if (cmp == 0) cmp = (a.recIdx < b.recIdx) ? -1 : 1;
                                return sortDescending ? (cmp > 0) : (cmp < 0);
                            });
                        results->clear();
                        for (const auto& pe : pathEntries) results->push_back(pe.recIdx);
                    } catch (const SortAbortedException&) {}
                }
            } else {
                try {
                    int cmpCount = 0;
                    std::sort(results->begin(), results->end(), [this, sortKey, sortDescending, &cmpCount](uint32_t a, uint32_t b) {
                        if ((++cmpCount & 0x3F) == 0 && m_isSearchRequested.load(std::memory_order_relaxed)) throw SortAbortedException();
                        const auto& ra = m_recordsShared[a];
                        const auto& rb = m_recordsShared[b];
                        
                        int primary = 0;
                        if (sortKey == QuerySortKey::Size) {
                            uint64_t sa = ResolveFileSize(ra, a);
                            uint64_t sb = ResolveFileSize(rb, b);
                            if (sa < sb) primary = -1; else if (sa > sb) primary = 1;
                        } else if (sortKey == QuerySortKey::Date) {
                            if (ra.LastModifiedEpoch < rb.LastModifiedEpoch) primary = -1; else if (ra.LastModifiedEpoch > rb.LastModifiedEpoch) primary = 1;
                        }

                        if (primary == 0) {
                            primary = FastCompareIgnoreCase(m_pool.GetString(ra.NamePoolOffset), m_pool.GetString(rb.NamePoolOffset));
                        }
                        if (primary == 0) primary = (a < b ? -1 : 1);
                        
                        return sortDescending ? (primary > 0) : (primary < 0);
                    });
                } catch (const SortAbortedException&) {
                    // Sort aborted
                }
            }
        }

        if (m_isSearchRequested) continue;

        bool changed = true;
        {
            std::lock_guard<std::mutex> lock(m_resultBufferMutex);
            if (m_currentResults && m_currentResults->size() == results->size()) {
                if (results->empty() || memcmp(m_currentResults->data(), results->data(), results->size() * sizeof(uint32_t)) == 0) {
                    changed = false;
                }
            }
            m_currentResults = std::move(results);
        }
        
        m_resultCv.notify_all();
        
        if (m_hwndNotify && changed) {
            PostMessage((HWND)m_hwndNotify, WM_USER_SEARCH_FINISHED, 0, 0);
        }
    }
}

void IndexingEngine::EnqueueUsnDelta(PendingUsnDelta&& delta) {
    std::lock_guard<std::mutex> lock(m_usnDeltaMutex);
    m_pendingUsnDeltas.emplace_back(std::move(delta));
}

void IndexingEngine::ApplyPendingUsnDeltas() {
    std::vector<PendingUsnDelta> deltas;
    {
        std::lock_guard<std::mutex> lock(m_usnDeltaMutex);
        if (m_pendingUsnDeltas.empty()) return;
        deltas.swap(m_pendingUsnDeltas);
    }

    // Phase 1 (no lock): pre-convert all incoming names to UTF-8.
    // WideCharToMultiByte can be slow under heavy load; doing it here avoids tying up
    // the exclusive write lock during CPU-bound string conversion.
    std::vector<std::string> utf8Names(deltas.size());
    for (size_t i = 0; i < deltas.size(); ++i)
        if (deltas[i].Type == PendingUsnDelta::Kind::Upsert)
            utf8Names[i] = WideToUtf8(deltas[i].Name);

    // Phase 2: apply under exclusive lock in epochs of 256 to keep lock windows bounded.
    // Yielding between epochs allows SearchThread and UI reads to proceed during
    // massive file-system events (e.g., node_modules installs creating tens of thousands
    // of files at once).
    constexpr size_t kEpochSize = 256;
    std::unique_lock<std::shared_mutex> lock(m_dataMutex);
    if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
    for (size_t i = 0; i < deltas.size(); ++i) {
        const auto& d = deltas[i];
        const uint8_t di = d.DriveIndex;
        if (di >= m_mftLookupTables.size()) continue;
        if (d.Type == PendingUsnDelta::Kind::Delete) {
            if (d.MftIndex < (uint32_t)m_mftLookupTables[di].size()) {
                uint32_t idx = m_mftLookupTables[di][d.MftIndex];
                if (idx != 0xFFFFFFFF && idx < GetRecordCount() && m_recordsShared[idx].MftSequence == d.MftSequence) {
                    m_recordsShared[idx].MftIndex = 0xFFFFFFFF;
                    m_mftLookupTables[di][d.MftIndex] = 0xFFFFFFFF;
                    m_giantFileSizes.erase(idx);
                    
                    if (!m_preSortedByName.empty()) {
                        auto it = std::lower_bound(m_preSortedByName.begin(), m_preSortedByName.end(), idx, [this](uint32_t a, uint32_t b) {
                            int cmp = FastCompareIgnoreCase(m_pool.GetString(m_recordsShared[a].NamePoolOffset), m_pool.GetString(m_recordsShared[b].NamePoolOffset));
                            return cmp != 0 ? (cmp < 0) : (a < b);
                        });
                        if (it != m_preSortedByName.end() && *it == idx) {
                            m_preSortedByName.erase(it);
                        } else {
                            auto lin = std::find(m_preSortedByName.begin(), m_preSortedByName.end(), idx);
                            if (lin != m_preSortedByName.end()) m_preSortedByName.erase(lin);
                        }
                    }
                }
            }
            continue;
        }

        const std::string& incomingNameUtf8 = utf8Names[i];
        uint32_t existing = 0xFFFFFFFF;
        if (d.MftIndex < (uint32_t)m_mftLookupTables[di].size()) existing = m_mftLookupTables[di][d.MftIndex];

        uint32_t nameOffset = 0;
        if (existing != 0xFFFFFFFF && existing < GetRecordCount()) {
            const char* existingNameUtf8 = m_pool.GetString(m_recordsShared[existing].NamePoolOffset);
            if (existingNameUtf8 && incomingNameUtf8 == existingNameUtf8) nameOffset = m_recordsShared[existing].NamePoolOffset;
        }
        if (nameOffset == 0) {
            if (incomingNameUtf8.empty()) continue;  // guard matches AddString's bytesNeeded<=1 path
            try { nameOffset = m_pool.AddRawData(incomingNameUtf8.c_str(), incomingNameUtf8.size() + 1); }
            catch (const std::bad_alloc&) { continue; }
        }

        FileRecord rec = {};
        rec.NamePoolOffset = nameOffset;
        rec.ParentMftIndex = d.ParentMftIndex;
        rec.MftIndex = d.MftIndex;
        rec.LastModifiedEpoch = FileTimeToEpoch(d.LastModified);
        rec.FileSize = (d.FileSize >= 0xFFFFFFFFULL) ? 0xFFFFFFFFu : (uint32_t)d.FileSize;
        rec.MftSequence = d.MftSequence;
        rec.ParentSequence = d.ParentSequence;
        rec.DriveIndex = di;
        rec.IsGiantFile = d.FileSize >= 0xFFFFFFFFULL ? 1u : 0u;
        rec.FileAttributes = d.FileAttributes;
        rec.Reserved = 0;
        rec.ParentRecordIndex = 0xFFFFFFFF;
        if (d.ParentMftIndex < (uint32_t)m_mftLookupTables[di].size()) {
            uint32_t pIdx = m_mftLookupTables[di][d.ParentMftIndex];
            if (pIdx != 0xFFFFFFFF && pIdx < GetRecordCount() && 
                m_recordsShared[pIdx].MftSequence == d.ParentSequence) {
                rec.ParentRecordIndex = pIdx;
            }
        }
        if (d.MftIndex >= (uint32_t)m_mftLookupTables[di].size()) {
            try { m_mftLookupTables[di].resize(d.MftIndex + 10000, 0xFFFFFFFF); }
            catch (const std::bad_alloc&) { continue; }
        }
        if (existing != 0xFFFFFFFF && existing < GetRecordCount()) {
            if (m_recordsShared[existing].MftSequence <= d.MftSequence) {
                bool nameChanged = (m_recordsShared[existing].NamePoolOffset != rec.NamePoolOffset);
                if (nameChanged && !m_preSortedByName.empty()) {
                    auto it = std::lower_bound(m_preSortedByName.begin(), m_preSortedByName.end(), existing, [this](uint32_t a, uint32_t b) {
                        int cmp = FastCompareIgnoreCase(m_pool.GetString(m_recordsShared[a].NamePoolOffset), m_pool.GetString(m_recordsShared[b].NamePoolOffset));
                        return cmp != 0 ? (cmp < 0) : (a < b);
                    });
                    if (it != m_preSortedByName.end() && *it == existing) m_preSortedByName.erase(it);
                    else {
                        auto lin = std::find(m_preSortedByName.begin(), m_preSortedByName.end(), existing);
                        if (lin != m_preSortedByName.end()) m_preSortedByName.erase(lin);
                    }
                }
                
                m_recordsShared[existing] = rec;
                
                if (nameChanged && !m_preSortedByName.empty()) {
                    auto it = std::lower_bound(m_preSortedByName.begin(), m_preSortedByName.end(), existing, [this](uint32_t a, uint32_t b) {
                        int cmp = FastCompareIgnoreCase(m_pool.GetString(m_recordsShared[a].NamePoolOffset), m_pool.GetString(m_recordsShared[b].NamePoolOffset));
                        return cmp != 0 ? (cmp < 0) : (a < b);
                    });
                    m_preSortedByName.insert(it, existing);
                }
                
                if (rec.IsGiantFile) m_giantFileSizes[existing] = d.FileSize;
                else m_giantFileSizes.erase(existing);
            }
        } else {
            uint32_t idx = (uint32_t)GetRecordCount();
            try {
                {
                    uint32_t __newIdx = m_recordsCount ? m_recordsCount->fetch_add(1, std::memory_order_relaxed) : 0;
                    if (m_recordsShared && __newIdx < 20000000ULL) m_recordsShared[__newIdx] = rec;
                }
                if (!m_preSortedByName.empty()) {
                    auto it = std::lower_bound(m_preSortedByName.begin(), m_preSortedByName.end(), idx, [this](uint32_t a, uint32_t b) {
                        int cmp = FastCompareIgnoreCase(m_pool.GetString(m_recordsShared[a].NamePoolOffset), m_pool.GetString(m_recordsShared[b].NamePoolOffset));
                        return cmp != 0 ? (cmp < 0) : (a < b);
                    });
                    m_preSortedByName.insert(it, idx);
                }
            } catch (const std::bad_alloc&) {
                continue;
            }
            m_mftLookupTables[di][d.MftIndex] = idx;
            if (rec.IsGiantFile) m_giantFileSizes[idx] = d.FileSize;
        }

        // Yield the exclusive lock every kEpochSize operations so readers can proceed.
        if ((i & (kEpochSize - 1)) == (kEpochSize - 1) && i + 1 < deltas.size()) {
            if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
            lock.unlock();
            lock.lock();
            if (m_hDataMutex) WaitForSingleObject(m_hDataMutex, INFINITE);
        }
    }
    if (m_hDataMutex) ReleaseMutex(m_hDataMutex);
}

void IndexingEngine::HandleUsnJournalRecord(USN_RECORD_V2* r, uint8_t di) {
    uint32_t mftIdx = (uint32_t)(r->FileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t seq = (uint16_t)(r->FileReferenceNumber >> 48);
    uint32_t pIdx = (uint32_t)(r->ParentFileReferenceNumber & 0xFFFFFFFFFFFFLL);
    uint16_t pSeq = (uint16_t)(r->ParentFileReferenceNumber >> 48);

    if (r->Reason & USN_REASON_FILE_DELETE) {
        PendingUsnDelta d;
        d.Type = PendingUsnDelta::Kind::Delete;
        d.DriveIndex = di;
        d.MftIndex = mftIdx;
        d.MftSequence = seq;
        EnqueueUsnDelta(std::move(d));
    }
    
    if (r->Reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME | USN_REASON_DATA_EXTEND | USN_REASON_DATA_TRUNCATION | USN_REASON_CLOSE)) {
        std::wstring name((LPCWSTR)((uint8_t*)r + r->FileNameOffset), r->FileNameLength/2);
        uint64_t fileSize = 0, lastMod = 0;

        // #4: Build fullPath under short shared lock, then do filesystem I/O OUTSIDE the lock.
        // Previously GetFileAttributesExW was called while holding shared_lock — any slow disk
        // (HDD/network/AV scan) would block all readers including SearchThread and paint.
        std::wstring fullPath;
        {
            std::shared_lock<std::shared_mutex> lock(m_dataMutex);
            if (pIdx < (uint32_t)m_mftLookupTables[di].size()) {
                uint32_t parentRecIdx = m_mftLookupTables[di][pIdx];
                if (parentRecIdx != 0xFFFFFFFF) {
                    fullPath = GetFullPathInternal(parentRecIdx) + L"\\" + name;
                }
            }
            if (fullPath.empty()) fullPath = m_drives[di].Letter + name;
        } // shared lock released before filesystem call

        WIN32_FILE_ATTRIBUTE_DATA att;
        if (GetFileAttributesExW(fullPath.c_str(), GetFileExInfoStandard, &att)) {
            fileSize = ((uint64_t)att.nFileSizeHigh << 32) | att.nFileSizeLow;
            lastMod  = ((uint64_t)att.ftLastWriteTime.dwHighDateTime << 32) | att.ftLastWriteTime.dwLowDateTime;
        }

        PendingUsnDelta d;
        d.Type = PendingUsnDelta::Kind::Upsert;
        d.DriveIndex = di;
        d.MftIndex = mftIdx;
        d.MftSequence = seq;
        d.ParentMftIndex = pIdx;
        d.ParentSequence = pSeq;
        d.Name = std::move(name);
        d.FileSize = fileSize;
        d.LastModified = lastMod;
        d.FileAttributes = (uint16_t)r->FileAttributes;
        EnqueueUsnDelta(std::move(d));
    }
}

void IndexingEngine::MonitorChanges() {
    auto buf = std::make_unique<uint8_t[]>(65536);

    // Build the wait-handle array:
    // Slot 0 is always m_stopEvent (so Stop() wakes us immediately).
    // Slots 1..N are FindFirstChangeNotification handles for each NTFS drive.
    std::vector<HANDLE> waitHandles;
    waitHandles.push_back(m_stopEvent);  // index 0 = stop signal

    for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
        auto& d = m_drives[i];
        if (d.VolumeHandle == INVALID_HANDLE_VALUE || d.Type != DriveFileSystem::NTFS) continue;
        HANDLE hChange = FindFirstChangeNotificationW(d.Letter.c_str(), TRUE,
            FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME |
            FILE_NOTIFY_CHANGE_ATTRIBUTES | FILE_NOTIFY_CHANGE_SIZE |
            FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_CREATION);
        if (hChange != INVALID_HANDLE_VALUE)
            waitHandles.push_back(hChange);
    }

    while (m_running) {
        // Fully event-driven: block until one of:
        //   [0] m_stopEvent   — Stop() was called
        //   [1..N] change notifications — filesystem changed
        // No polling, no busy loops, no CPU usage while idle, zero debounce delay.
        DWORD waitMs = INFINITE;  // block forever until a real event fires
        DWORD waitRes = WaitForMultipleObjects(
            (DWORD)waitHandles.size(), waitHandles.data(), FALSE, waitMs);

        if (!m_running || waitRes == WAIT_OBJECT_0) break;  // stop event
        if (waitRes == WAIT_FAILED) break;

        // A filesystem change notification fired. Poll USN journal to absorb it.
        bool anyChanges = false;
        for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
            auto& d = m_drives[i];
            if (d.VolumeHandle == INVALID_HANDLE_VALUE || d.Type != DriveFileSystem::NTFS) continue;
            USN_JOURNAL_DATA_V0 uj; DWORD cb;
            if (!DeviceIoControl(d.VolumeHandle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) continue;
            READ_USN_JOURNAL_DATA_V0 rd = { (USN)d.LastProcessedUsn, 0xFFFFFFFF, FALSE, 0, 0, uj.UsnJournalID };
            if (DeviceIoControl(d.VolumeHandle, FSCTL_READ_USN_JOURNAL, &rd, sizeof(rd), buf.get(), 65536, &cb, NULL)) {
                if (cb > sizeof(USN)) {
                    uint8_t* p = buf.get() + sizeof(USN);
                    while (p < buf.get() + cb) {
                        USN_RECORD_V2* record = (USN_RECORD_V2*)p;
                        HandleUsnJournalRecord(record, i);
                        p += record->RecordLength;
                    }
                    d.LastProcessedUsn = (uint64_t)(*(USN*)buf.get());
                    anyChanges = true;
                }
            } else if (GetLastError() == ERROR_JOURNAL_ENTRY_DELETED) {
                SetStatus(L"Journal Truncated. Sync lost.");
            }
        }

        if (anyChanges) {
            ApplyPendingUsnDeltas();
            // UpdatePreSortedIndex() is no longer needed here since ApplyPendingUsnDeltas dynamically maintains the list.
            
            // Throttle UI search trigger for background changes to 10Hz to prevent listview flicker.
            auto now = std::chrono::steady_clock::now();
            if (now - m_lastUsnMerge >= std::chrono::milliseconds(100)) {
                std::lock_guard<std::mutex> lock(m_searchSyncMutex);
                m_isSearchRequested = true;
                m_searchEvent.notify_one();
                m_lastUsnMerge = now;
            }
        }

        // Re-arm ONLY the handle that fired.
        // Calling FindNextChangeNotification on un-signaled handles causes infinite spin.
        if (waitRes >= WAIT_OBJECT_0 + 1 && waitRes < WAIT_OBJECT_0 + waitHandles.size()) {
            FindNextChangeNotification(waitHandles[waitRes - WAIT_OBJECT_0]);
        }
    }

    ApplyPendingUsnDeltas();

    // Cleanup
    for (size_t hi = 1; hi < waitHandles.size(); ++hi) {
        if (waitHandles[hi] != nullptr)
            FindCloseChangeNotification(waitHandles[hi]);
    }
}

bool IndexingEngine::DiscoverAllDrives() {
    SetStatus(L"Discovering drives...");
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
            SetStatus(L"Indexing... " + FormatNumberWithCommas(totalFound.load()) + L" items");
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    });

    for (auto& t : workers) t.join();
    if (progressThread.joinable()) progressThread.join();

    {
        std::unique_lock<std::shared_mutex> lock(m_dataMutex);
        if (m_recordsCount) m_recordsCount->store(0, std::memory_order_relaxed); m_pool.Clear(); m_mftLookupTables.clear(); m_giantFileSizes.clear();
        {
            std::lock_guard<std::mutex> cacheLock(m_nameCacheMutex);
            m_nameCache.clear();
            m_nameCacheLru.clear();
        }
        m_mftLookupTables.resize(m_drives.size());

        for (uint8_t i = 0; i < (uint8_t)m_drives.size(); ++i) {
            auto& ctx = contexts[i];
            // Merge per-drive string pool into the global pool by adopting chunks directly.
            uint32_t poolOffsetShift = m_pool.AdoptChunksFrom(ctx.Pool);
            uint32_t recordIndexShift = (uint32_t)GetRecordCount();

            m_mftLookupTables[i] = std::move(ctx.LookupTable);
            
            for (uint32_t localRecordIdx = 0; localRecordIdx < (uint32_t)ctx.Records.size(); ++localRecordIdx) {
                auto& rec = ctx.Records[localRecordIdx];
                uint32_t globalRecordIdx = (uint32_t)GetRecordCount();
                if (rec.IsGiantFile) {
                    auto giantIt = ctx.GiantFileSizes.find(localRecordIdx);
                    if (giantIt != ctx.GiantFileSizes.end()) m_giantFileSizes[globalRecordIdx] = giantIt->second;
                }
                rec.NamePoolOffset += poolOffsetShift;
                if (ctx.Type != DriveFileSystem::NTFS) {
                    if (rec.ParentMftIndex != 0xFFFFFFFF) rec.ParentMftIndex += recordIndexShift;
                    uint32_t localIdx = rec.MftIndex; 
                    rec.MftIndex = recordIndexShift + localIdx;
                    if (localIdx >= m_mftLookupTables[i].size()) m_mftLookupTables[i].resize(localIdx + 1, 0xFFFFFFFF);
                    m_mftLookupTables[i][localIdx] = rec.MftIndex;
                }
                {
                    uint32_t __newIdx = m_recordsCount ? m_recordsCount->fetch_add(1, std::memory_order_relaxed) : 0;
                    if (m_recordsShared && __newIdx < 20000000ULL) m_recordsShared[__newIdx] = rec;
                }
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
    PropagateDirectorySizes();
}
void IndexingEngine::PropagateDirectorySizes() {
    // Bottom-up accumulation: iterate records in reverse-index order (which is
    // approximately reverse-MFT order — children tend to have higher MFT indices
    // than their parents).  For each file, add its resolved size to the FileSize
    // field of its parent directory record.  Giant directories (accumulated size
    // >= 4 GB) are promoted to m_giantFileSizes.
    //
    // Called while m_dataMutex is already held exclusively from PerformFullDriveScan.
    for (int64_t i = (int64_t)GetRecordCount() - 1; i >= 0; --i) {
        FileRecord& child = m_recordsShared[(uint32_t)i];
        
        uint32_t parentIdx = 0xFFFFFFFF;
        uint8_t di = (uint8_t)child.DriveIndex;
        if (di < (uint8_t)m_mftLookupTables.size() && child.ParentMftIndex < (uint32_t)m_mftLookupTables[di].size()) {
            parentIdx = m_mftLookupTables[di][child.ParentMftIndex];
            if (parentIdx != 0xFFFFFFFF && m_recordsShared[parentIdx].MftSequence != child.ParentSequence) {
                parentIdx = 0xFFFFFFFF;
            }
        }
        child.ParentRecordIndex = parentIdx;

        if (child.MftIndex == 0xFFFFFFFF) continue;        // deleted record slot
        if (child.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue; // dirs themselves

        if (parentIdx == 0xFFFFFFFF || parentIdx >= GetRecordCount()) continue;

        FileRecord& parent = m_recordsShared[parentIdx];
        if (!(parent.FileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;

        // Resolve the child's actual file size (giant files stored separately).
        uint64_t childSize = ResolveFileSize(child, (uint32_t)i);

        // Accumulate into parent directory size.
        uint64_t parentSize = ResolveFileSize(parent, parentIdx);
        uint64_t newSize    = parentSize + childSize;

        if (newSize >= 0xFFFFFFFFULL) {
            parent.FileSize  = 0xFFFFFFFFu;
            parent.IsGiantFile = 1;
            m_giantFileSizes[parentIdx] = newSize;
        } else {
            parent.FileSize = (uint32_t)newSize;
            // If it was previously a giant but accumulated size dropped below, clean up.
            if (parent.IsGiantFile && m_giantFileSizes.count(parentIdx)) {
                m_giantFileSizes.erase(parentIdx);
                parent.IsGiantFile = 0;
            }
        }
        parent.DirSizeComputed = 1;
    }
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
            uint64_t fullSize = ((uint64_t)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
            uint64_t fullTime = ((uint64_t)fd.ftLastWriteTime.dwHighDateTime << 32) | fd.ftLastWriteTime.dwLowDateTime;
            FileRecord rec = {};
            rec.NamePoolOffset = ctx.Pool.AddString(fd.cFileName);
            rec.ParentMftIndex = current.pIdx;
            rec.MftIndex = myLocalIdx;
            rec.LastModifiedEpoch = FileTimeToUnixEpochSeconds(fullTime);
            rec.FileSize = fullSize >= 0xFFFFFFFFULL ? 0xFFFFFFFFu : (uint32_t)fullSize;
            rec.MftSequence = 0;
            rec.ParentSequence = current.pSeq;
            rec.DriveIndex = ctx.DriveIndex;
            rec.IsGiantFile = fullSize >= 0xFFFFFFFFULL ? 1u : 0u;
            rec.FileAttributes = (uint16_t)fd.dwFileAttributes;
            rec.Reserved = 0;
            rec.ParentRecordIndex = 0xFFFFFFFF;
            ctx.Records.push_back(rec);
            if (rec.IsGiantFile) ctx.GiantFileSizes[myLocalIdx] = fullSize;

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
            // $ATTRIBUTE_LIST entry — each entry lists an attribute and which MFT record holds it.
            #pragma pack(push, 1)
            struct AttrListEntry {
                uint32_t Type; uint16_t Length; uint8_t NameLength; uint8_t NameOffset;
                uint64_t LowestVcn; uint64_t MftRef; uint16_t AttributeId;
            };
            #pragma pack(pop)

            struct MFT_NR { MFT_ATTRIBUTE h; uint64_t startVcn, lastVcn; uint16_t runOffset; }* nr = (MFT_NR*)a;
            uint8_t* rlBase = (uint8_t*)a + nr->runOffset;

            // Store the MFT run list for targeted random-access reads during the post-scan pass.
            struct MftRun { uint64_t vcnStart; int64_t lcnStart; uint64_t length; };
            std::vector<MftRun> mftRuns;
            {
                uint8_t* rl2 = rlBase; uint64_t vcn = 0; int64_t lcn = 0;
                while (*rl2) {
                    uint8_t hb = *rl2++; int ls = hb & 0xF, os = hb >> 4;
                    uint64_t len = 0; for (int j = 0; j < ls; j++) len |= (uint64_t)(*rl2++) << (j * 8);
                    int64_t runOff = 0; for (int j = 0; j < os; j++) runOff |= (int64_t)(*rl2++) << (j * 8);
                    if (os > 0 && (runOff >> (os * 8 - 1)) & 1) for (int j = os; j < 8; j++) runOff |= (int64_t)0xFF << (j * 8);
                    lcn += runOff; mftRuns.push_back({vcn, lcn, len}); vcn += len;
                }
            }

            // Read a single MFT record by its logical index into a pre-allocated buffer.
            auto readMftRecord = [&](uint32_t mftIdx, std::vector<uint8_t>& buf) -> bool {
                uint64_t byteOff = (uint64_t)mftIdx * nt.BytesPerFileRecordSegment;
                uint64_t vcn = byteOff / nt.BytesPerCluster;
                uint64_t vcnOff = byteOff % nt.BytesPerCluster;
                for (const auto& run : mftRuns) {
                    if (vcn >= run.vcnStart && vcn < run.vcnStart + run.length) {
                        int64_t lcn = run.lcnStart + (int64_t)(vcn - run.vcnStart);
                        LARGE_INTEGER seekPos; seekPos.QuadPart = lcn * nt.BytesPerCluster + (int64_t)vcnOff;
                        DWORD br2 = 0;
                        if (!SetFilePointerEx(ctx.VolumeHandle, seekPos, NULL, FILE_BEGIN)) return false;
                        if (!ReadFile(ctx.VolumeHandle, buf.data(), nt.BytesPerFileRecordSegment, &br2, NULL)) return false;
                        return br2 == (DWORD)nt.BytesPerFileRecordSegment;
                    }
                }
                return false;
            };

            // Apply NTFS update-sequence-array fixup to a record buffer in place.
            auto applyFixup = [&](uint8_t* recBuf) {
                MFT_RECORD_HEADER* rh = (MFT_RECORD_HEADER*)recBuf;
                if (rh->UpdateSeqOffset + rh->UpdateSeqSize * (uint32_t)sizeof(uint16_t) <= nt.BytesPerFileRecordSegment) {
                    uint16_t* usa = (uint16_t*)(recBuf + rh->UpdateSeqOffset);
                    for (uint16_t s = 1; s < rh->UpdateSeqSize; s++) {
                        if (s * 512u > nt.BytesPerFileRecordSegment) break;
                        uint16_t* sectorEnd = (uint16_t*)(recBuf + s * 512 - 2);
                        if (*sectorEnd == usa[0]) *sectorEnd = usa[s];
                    }
                }
            };

            // Commit a 0x30 $FILE_NAME attribute to ctx.Records under effectiveMftIdx.
            auto commitFileName = [&](uint8_t* recBuf, uint32_t attrOff, uint32_t effectiveMftIdx, uint16_t seq, uint16_t rhFlags) {
                MFT_ATTRIBUTE* aa = (MFT_ATTRIBUTE*)(recBuf + attrOff);
                if (aa->Type != 0x30 || aa->NonResident) return;
                MFT_FILE_NAME* fn = (MFT_FILE_NAME*)(recBuf + attrOff + ((MFT_RESIDENT_ATTRIBUTE*)aa)->ValueOffset);
                if (fn->NameNamespace == 2) return;  // skip DOS-only short names
                FileRecord entry = {};
                entry.NamePoolOffset   = ctx.Pool.AddString(fn->Name, fn->NameLength);
                entry.ParentMftIndex   = (uint32_t)(fn->ParentDirectory & 0xFFFFFFFFFFFFLL);
                entry.MftIndex         = effectiveMftIdx;
                entry.LastModifiedEpoch = FileTimeToUnixEpochSeconds(fn->LastWriteTime);
                entry.FileSize         = fn->DataSize >= 0xFFFFFFFFULL ? 0xFFFFFFFFu : (uint32_t)fn->DataSize;
                entry.MftSequence      = seq;
                entry.ParentSequence   = (uint16_t)(fn->ParentDirectory >> 48);
                entry.DriveIndex       = ctx.DriveIndex;
                entry.IsGiantFile      = fn->DataSize >= 0xFFFFFFFFULL ? 1u : 0u;
                entry.FileAttributes   = (uint16_t)fn->FileAttributes;
                entry.Reserved = 0; entry.ParentRecordIndex = 0xFFFFFFFF;
                if (rhFlags & 0x02) entry.FileAttributes |= 0x10;
                uint32_t ri = (uint32_t)ctx.Records.size(); ctx.Records.push_back(entry);
                if (entry.IsGiantFile) ctx.GiantFileSizes[ri] = fn->DataSize;
                // Only set if vacant: base-record 0x30 is processed before extension records
                // (lower MFT index first), so a later extension must not overwrite it.
                if (effectiveMftIdx < (uint32_t)ctx.LookupTable.size() && ctx.LookupTable[effectiveMftIdx] == 0xFFFFFFFF)
                    ctx.LookupTable[effectiveMftIdx] = ri;
            };

            // Maps extension MFT index -> (base MFT index, base sequence number).
            // Populated while parsing 0x20 attribute lists in base records.
            std::unordered_map<uint32_t, std::pair<uint32_t, uint16_t>> pendingExtensions;
            // Extension records whose 0x30 was already committed during the linear scan.
            std::unordered_set<uint32_t> processedExtensions;

            uint8_t* rl = rlBase; int64_t curLcn = 0; uint32_t mftCounter = 0;
            std::vector<uint8_t> eb(1024 * 1024);
            std::vector<uint8_t> extBuf(nt.BytesPerFileRecordSegment);

            while (*rl) {
                uint8_t hb = *rl++; int ls = hb & 0xF, os = hb >> 4;
                uint64_t len = 0; for (int j=0; j<ls; j++) len |= (uint64_t)(*rl++) << (j*8);
                int64_t runOff = 0; for (int j=0; j<os; j++) runOff |= (int64_t)(*rl++) << (j*8);
                if (os > 0 && (runOff >> (os*8-1))&1) for (int j=os; j<8; j++) runOff |= (int64_t)0xFF << (j*8);
                curLcn += runOff; LARGE_INTEGER eo; eo.QuadPart = curLcn * nt.BytesPerCluster;
                SetFilePointerEx(ctx.VolumeHandle, eo, NULL, FILE_BEGIN); uint64_t bt = len * nt.BytesPerCluster;
                DWORD br;
                for (uint64_t r = 0; r < bt; r += br) {
                    if (!ReadFile(ctx.VolumeHandle, eb.data(), (DWORD)min(bt - r, 1024 * 1024ULL), &br, NULL) || !br) break;
                    for (uint32_t k = 0; k + nt.BytesPerFileRecordSegment <= br; k += nt.BytesPerFileRecordSegment) {
                        uint32_t tm = mftCounter++; MFT_RECORD_HEADER* rh = (MFT_RECORD_HEADER*)&eb[k];
                        if (rh->Magic != 0x454C4946 || !(rh->Flags & 0x01)) continue;
                        applyFixup(&eb[k]);

                        // Extension records carry their base record's MFT reference in BaseRecord.
                        // Use the base MFT index so parent-chain lookups resolve correctly.
                        bool isExtension = (rh->BaseRecord != 0);
                        uint32_t effectiveMftIdx = isExtension
                            ? (uint32_t)(rh->BaseRecord & 0xFFFFFFFFFFFFLL)
                            : tm;
                        uint16_t effectiveSeq = isExtension
                            ? (uint16_t)(rh->BaseRecord >> 48)
                            : rh->SequenceNumber;

                        uint32_t ao = rh->AttributeOffset;
                        while (ao + sizeof(MFT_ATTRIBUTE) < nt.BytesPerFileRecordSegment) {
                            MFT_ATTRIBUTE* aa = (MFT_ATTRIBUTE*)&eb[k + ao];
                            if (aa->Type == 0xFFFFFFFF) break;

                            if (aa->Type == 0x30 && !aa->NonResident) {
                                commitFileName(&eb[k], ao, effectiveMftIdx, effectiveSeq, rh->Flags);
                                if (isExtension) processedExtensions.insert(tm);
                            } else if (aa->Type == 0x20 && !isExtension && !aa->NonResident) {
                                // Parse $ATTRIBUTE_LIST: collect 0x30 attributes hosted in extension records
                                // so we can fetch any that the linear scan may miss.
                                MFT_RESIDENT_ATTRIBUTE* ra = (MFT_RESIDENT_ATTRIBUTE*)aa;
                                const uint8_t* le = &eb[k + ao + ra->ValueOffset];
                                const uint8_t* leEnd = le + ra->ValueLength;
                                while (le + sizeof(AttrListEntry) <= leEnd) {
                                    const AttrListEntry* ale = (const AttrListEntry*)le;
                                    if (ale->Length < sizeof(AttrListEntry)) break;
                                    if (ale->Type == 0x30) {
                                        uint32_t extMftIdx = (uint32_t)(ale->MftRef & 0xFFFFFFFFFFFFLL);
                                        if (extMftIdx != tm)  // attribute lives in an extension record
                                            pendingExtensions.try_emplace(extMftIdx, tm, rh->SequenceNumber);
                                    }
                                    le += ale->Length;
                                }
                            }
                            if (!aa->Length) break; ao += aa->Length;
                        }
                    }
                }
            }

            // Post-scan pass: fetch extension records whose 0x30 was not encountered during
            // the sequential read (e.g., extension record index precedes its base record).
            for (const auto& [extMftIdx, baseInfo] : pendingExtensions) {
                if (processedExtensions.count(extMftIdx)) continue;
                auto [baseMftIdx, baseSeq] = baseInfo;
                if (!readMftRecord(extMftIdx, extBuf)) continue;
                MFT_RECORD_HEADER* rh = (MFT_RECORD_HEADER*)extBuf.data();
                if (rh->Magic != 0x454C4946 || !(rh->Flags & 0x01)) continue;
                applyFixup(extBuf.data());
                uint32_t ao = rh->AttributeOffset;
                while (ao + sizeof(MFT_ATTRIBUTE) < nt.BytesPerFileRecordSegment) {
                    MFT_ATTRIBUTE* aa = (MFT_ATTRIBUTE*)&extBuf[ao];
                    if (aa->Type == 0xFFFFFFFF) break;
                    if (aa->Type == 0x30 && !aa->NonResident)
                        commitFileName(extBuf.data(), ao, baseMftIdx, baseSeq, rh->Flags);
                    if (!aa->Length) break; ao += aa->Length;
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
            if (d.Type == DriveFileSystem::NTFS && d.VolumeHandle != INVALID_HANDLE_VALUE) {
                USN_JOURNAL_DATA_V0 uj; DWORD cb;
                if (DeviceIoControl(d.VolumeHandle, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &uj, sizeof(uj), &cb, NULL)) {
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
    swprintf_s(debugBuf, L"[WhereIsIt] Worker thread ready. Total records: %s. Triggering initial search.\n", FormatNumberWithCommas(GetRecordCount()).c_str());
    Logger::Log(debugBuf);

    UpdatePreSortedIndex();

    SetStatus(L"Ready - " + FormatNumberWithCommas(GetRecordCount()) + L" items");
    m_ready = true;
    Search("");
    MonitorChanges();
}

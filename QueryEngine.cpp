#include "framework.h"
#include "QueryEngine.h"
#include "StringUtils.h"
#include <algorithm>
#include <cctype>
#include <limits>
#include <immintrin.h>
#include <intrin.h>
#include <windows.h>

// Extension whitelists for file-type quick-filters.
// Defined here so BuildQueryPlan can populate QueryConfig.ExtWhitelist
// without exposing them to the UI layer. TriggerSearch emits "extfilt:audio" etc.
static const char* s_audioExts[] = {
    "mp3","flac","aac","wav","ogg","m4a","wma","opus","aiff","alac",
    "ape","oga","wv","mid","midi", nullptr
};

static const char* s_codeExts[]       = {
    "c","cpp","cc","cxx","h","hpp","hh","hxx","h","inl",
    "py","pyw","pyc",
    "java","class",
    "js","jsx","ts","tsx","mjs","cjs",
    "php","php3","php4","php5","phtml",
    "rb","erb","rake",
    "go",
    "rs",
    "swift",
    "cs","vb","vbs",
    "sh","bash","zsh","fish",
    "css","scss","sass","less",
    "html","htm","xhtml","shtml",
    "xml","xsd","xsl","wsdl",
    "json","yaml","yml",
    "sql","ddl",
    "lua","pl","pm","tcl",
    "asm","s","inc",
    "kt","kts","dart","scala",
    "r","R",
    nullptr
};

static const char* s_compressedExts[] = {
    "zip","7z","rar","tar","gz","xz","bz2","zst","cab","iso",
    "tgz","jar","dmg","deb","rpm", nullptr
};

static const char* s_documentExts[] = {
    "doc","docx","xls","xlsx","ppt","pptx","pdf","txt","rtf","odt","ods","odp","md","csv",
    "epub","html","htm","json", nullptr
};

static const char* s_executableExts[] = {
    "exe","dll","msi","bat","cmd","ps1","com","scr","vbs","wsf",
    "jar","sh","js","py", nullptr
};

static const char* s_pictureExts[] = {
    "jpg","jpeg","png","gif","bmp","tif","tiff","webp","heic","svg","ico","raw","cr2","nef","arw",
    "avif","heif","psd","dng", nullptr
};

static const char* s_videoExts[] = {
    "mp4","mkv","avi","mov","wmv","flv","webm","m4v","mpg","mpeg","ts",
    "m2ts","mts","ogv","3gp","vob", nullptr
};

const unsigned char g_ToLowerLookup[256] = {
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

int FastCompareIgnoreCase(const char* s1, const char* s2) {
    while (*s1 && (g_ToLowerLookup[(unsigned char)*s1] == g_ToLowerLookup[(unsigned char)*s2])) { 
        s1++; s2++; 
    }
    return (int)g_ToLowerLookup[(unsigned char)*s1] - (int)g_ToLowerLookup[(unsigned char)*s2];
}

std::string ToLowerAscii(std::string value) {
    for (char& c : value) c = (char)g_ToLowerLookup[(unsigned char)c];
    return value;
}

bool FastContains(const char* haystack, const std::string& needle, bool caseSensitive) {
    if (needle.empty()) return true;
    if (caseSensitive) return strstr(haystack, needle.c_str()) != nullptr;
    return FastContainsIgnoreCase(haystack, needle.c_str());
}

bool FastContainsCIPtr(const char* haystack, const char* needleLow, size_t needleLen) {
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
    size_t remaining = strlen(haystack);
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

bool IsWordBoundary(const char* text, size_t len, size_t pos) {
    if (pos >= len) return true;
    unsigned char c = (unsigned char)text[pos];
    return !(std::isalnum(c) || c == '_');
}

bool ContainsWholeWord(const char* haystack, const std::string& needle, bool caseSensitive) {
    if (needle.empty()) return true;
    const size_t hLen = strlen(haystack), nLen = needle.size();
    if (nLen > hLen) return false;
    for (size_t i = 0; i <= hLen - nLen; ++i) {
        bool match = true;
        for (size_t j = 0; j < nLen && match; ++j) {
            unsigned char hc = (unsigned char)haystack[i + j];
            unsigned char nc = (unsigned char)needle[j];
            match = caseSensitive ? (hc == nc) : (g_ToLowerLookup[hc] == g_ToLowerLookup[nc]);
        }
        if (match) {
            bool left  = (i == 0)            || IsWordBoundary(haystack, hLen, i - 1);
            bool right = (i + nLen >= hLen)  || IsWordBoundary(haystack, hLen, i + nLen);
            if (left && right) return true;
        }
    }
    return false;
}

static bool ParseBool(const std::string& raw) {
    std::string value = ToLowerAscii(raw);
    return value == "1" || value == "true" || value == "yes" || value == "on";
}

bool ParseSizeBytes(const std::string& raw, uint64_t& out) {
    std::string text = ToLowerAscii(raw);
    text.erase(std::remove_if(text.begin(), text.end(), [](unsigned char c) { return std::isspace(c) != 0; }), text.end());
    static const std::regex kSizePattern(R"(^([0-9]+)(b|kb|mb|gb)?$)", std::regex_constants::ECMAScript);
    std::smatch match;
    if (!std::regex_match(text, match, kSizePattern) || match.size() < 3) return false;

    uint64_t mult = 1;
    const std::string suffix = match[2].str();
    if (suffix == "kb") mult = 1024ULL;
    else if (suffix == "mb") mult = 1024ULL * 1024ULL;
    else if (suffix == "gb") mult = 1024ULL * 1024ULL * 1024ULL;
    else if (suffix.empty() || suffix == "b") mult = 1ULL;
    else return false;

    try {
        uint64_t base = std::stoull(match[1].str());
        if (base > (std::numeric_limits<uint64_t>::max() / mult)) return false;
        out = base * mult;
    } catch (...) {
        return false;
    }
    return true;
}

bool ParseDateToFileTime(const std::string& raw, uint64_t& out) {
    int y = 0, m = 0, d = 0;
    if (sscanf_s(raw.c_str(), "%d-%d-%d", &y, &m, &d) != 3) return false;
    SYSTEMTIME st = { 0 }; st.wYear = (WORD)y; st.wMonth = (WORD)m; st.wDay = (WORD)d;
    FILETIME ft;
    if (!SystemTimeToFileTime(&st, &ft)) return false;
    out = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    return true;
}

uint32_t FileTimeToUnixEpochSeconds(uint64_t fileTime) {
    constexpr uint64_t kFileTimeTicksPerSecond = 10000000ULL;
    constexpr uint64_t kEpochDiffSeconds = 11644473600ULL;
    uint64_t seconds = fileTime / kFileTimeTicksPerSecond;
    if (seconds <= kEpochDiffSeconds) return 0;
    uint64_t epoch = seconds - kEpochDiffSeconds;
    return epoch > static_cast<uint64_t>(kInvalidIndex) ? kInvalidIndex : static_cast<uint32_t>(epoch);
}

uint64_t UnixEpochSecondsToFileTime(uint32_t epoch) {
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

QueryPlan BuildQueryPlan(const std::string& rawQuery) {
    QueryPlan plan;
    auto tokens = TokenizeQuery(rawQuery);
    std::vector<std::string> exprTokens;
    for (const auto& token : tokens) {
        std::string low = ToLowerAscii(token);
        if (low.rfind("case:", 0) == 0) { plan.Config.CaseSensitive = ParseBool(token.substr(5)); continue; }
        if (low.rfind("regex:", 0) == 0) { plan.Config.RegexMode = ParseBool(token.substr(6)); continue; }
        if (low.rfind("word:", 0) == 0) { plan.Config.WholeWord = ParseBool(token.substr(5)); continue; }
        if (low.rfind("matchpath:", 0) == 0) { plan.Config.MatchPath = ParseBool(token.substr(10)); continue; }
        if (low.rfind("diacritics:", 0) == 0) { plan.Config.MatchDiacritics = ParseBool(token.substr(11)); continue; }
        if (low.rfind("extfilt:", 0) == 0) {
            // File-type quick-filter token emitted by TriggerSearch().
            // Sets ExtWhitelist or FolderOnly on QueryConfig so the engine's fast loops
            // can apply the filter inline without constructing an OR-clause AST.
            std::string filt = low.substr(8);
            if      (filt == "audio")      plan.Config.ExtWhitelist = s_audioExts;
            else if (filt == "code")       plan.Config.ExtWhitelist = s_codeExts;
            else if (filt == "compressed") plan.Config.ExtWhitelist = s_compressedExts;
            else if (filt == "document")   plan.Config.ExtWhitelist = s_documentExts;
            else if (filt == "executable") plan.Config.ExtWhitelist = s_executableExts;
            else if (filt == "picture")    plan.Config.ExtWhitelist = s_pictureExts;
            else if (filt == "video")      plan.Config.ExtWhitelist = s_videoExts;
            else if (filt == "folder")     plan.Config.FolderOnly = true;
            continue;
        }
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
                std::wstring msg = Utf8ToWide(e.what());
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

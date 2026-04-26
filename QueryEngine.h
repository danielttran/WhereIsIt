#pragma once
#include <string>
#include <vector>
#include <memory>
#include <regex>
#include "CoreTypes.h"

extern const unsigned char g_ToLowerLookup[256];

std::string ToLowerAscii(std::string value);
bool IsWordBoundary(const std::string& text, size_t pos);
bool FastContainsCIPtr(const char* haystack, const char* needleLow, size_t needleLen);
bool FastContains(const std::string& haystack, const std::string& needle, bool caseSensitive);
bool ContainsWholeWord(const std::string& haystack, const std::string& needle, bool caseSensitive);
int FastCompareIgnoreCase(const char* s1, const char* s2);
uint32_t FileTimeToUnixEpochSeconds(uint64_t fileTime);
uint64_t UnixEpochSecondsToFileTime(uint32_t epoch);
bool ParseSizeBytes(const std::string& raw, uint64_t& out);
bool ParseDateToFileTime(const std::string& raw, uint64_t& out);

struct QueryConfig {
    bool CaseSensitive   = false;
    bool RegexMode       = false;
    bool WholeWord       = false;
    bool MatchPath       = false;   // apply every term against full path as well as filename
    bool MatchDiacritics = false;   // accent-sensitive (reserved for future Unicode fold)
    bool SortDescending  = false;
    bool FolderOnly      = false;   // show only directories
    QuerySortKey SortKey         = QuerySortKey::Name;
    const char* const* ExtWhitelist = nullptr;  // null-terminated list of allowed lowercase exts; null=no filter
};

struct CompiledPattern {
    std::regex re;
    CompiledPattern() = default;
    CompiledPattern(const std::string& pattern, std::regex_constants::syntax_option_type opts)
        : re(pattern, opts) {}
    bool match(const std::string& text) const { return std::regex_search(text, re); }
};

enum class QueryNodeType { Term, And, Or, Not };

struct QueryNode {
    QueryNodeType Type;
    std::string Term;      
    std::string TermLower; 
    std::unique_ptr<QueryNode> Left;
    std::unique_ptr<QueryNode> Right;
};

struct QueryPlan {
    QueryConfig Config;
    std::vector<std::string> Tokens;
    std::unique_ptr<QueryNode> Root;
    std::vector<CompiledPattern> CompiledRegex;
    bool Success = true;
    std::wstring ErrorMessage;
};

QueryPlan BuildQueryPlan(const std::string& rawQuery);

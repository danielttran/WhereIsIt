#pragma once

#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <vector>

namespace parity {

struct ResultRow {
    std::string caseId;
    int rank = 0;
    long long recordId = 0;
    std::string path;
    long long size = 0;
    long long modified = 0;
    long long attributes = 0;
    std::string raw;
};

struct Mismatch {
    std::string caseId;
    std::string mismatchType;
    std::string adminValue;
    std::string nonAdminValue;
    int firstIndex = 0;
    std::string suggestedOwner;
};

std::string EscapeJson(const std::string& s);
bool ParseRow(const std::string& line, ResultRow& row, std::string& err);
bool ReadJsonl(const std::filesystem::path& path, std::vector<ResultRow>& rows, std::string& err);
std::map<std::string, std::vector<ResultRow>> GroupByCase(std::vector<ResultRow> rows);
std::optional<Mismatch> DiffCase(const std::string& caseId,
                                 const std::vector<ResultRow>& admin,
                                 const std::vector<ResultRow>& nonAdmin);
std::vector<Mismatch> DiffAll(const std::map<std::string, std::vector<ResultRow>>& groupedAdmin,
                              const std::map<std::string, std::vector<ResultRow>>& groupedNonAdmin,
                              int& outTotalCases);
bool WriteDiffJson(const std::filesystem::path& path, const std::vector<Mismatch>& diffs, std::string& err);
bool WriteSummaryJson(const std::filesystem::path& path, int totalCases, const std::vector<Mismatch>& diffs, std::string& err);

} // namespace parity

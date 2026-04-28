#include "ParityComparatorLib.h"

#include <algorithm>
#include <fstream>
#include <regex>

namespace parity {

std::string EscapeJson(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 8);
    for (char c : s) {
        switch (c) {
        case '\\': out += "\\\\"; break;
        case '"': out += "\\\""; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default: out += c; break;
        }
    }
    return out;
}

static std::optional<std::string> ExtractString(const std::string& line, const std::string& key) {
    const std::regex re("\\\"" + key + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
    std::smatch m;
    if (!std::regex_search(line, m, re) || m.size() < 2) return std::nullopt;
    return m[1].str();
}

static std::optional<long long> ExtractInt(const std::string& line, const std::string& key) {
    const std::regex re("\\\"" + key + "\\\"\\s*:\\s*(-?[0-9]+)");
    std::smatch m;
    if (!std::regex_search(line, m, re) || m.size() < 2) return std::nullopt;
    try {
        return std::stoll(m[1].str());
    }
    catch (...) {
        return std::nullopt;
    }
}

bool ParseRow(const std::string& line, ResultRow& row, std::string& err) {
    auto caseId = ExtractString(line, "case_id");
    auto rank = ExtractInt(line, "rank");
    auto recordId = ExtractInt(line, "record_id");
    auto path = ExtractString(line, "path");
    auto size = ExtractInt(line, "size");
    auto modified = ExtractInt(line, "modified");
    auto attributes = ExtractInt(line, "attributes");

    if (!caseId || !rank || !recordId || !path || !size || !modified || !attributes) {
        err = "Missing one or more required fields (case_id, rank, record_id, path, size, modified, attributes).";
        return false;
    }

    row.caseId = *caseId;
    row.rank = static_cast<int>(*rank);
    row.recordId = *recordId;
    row.path = *path;
    row.size = *size;
    row.modified = *modified;
    row.attributes = *attributes;
    row.raw = line;
    return true;
}

bool ReadJsonl(const std::filesystem::path& path, std::vector<ResultRow>& rows, std::string& err) {
    std::ifstream in(path);
    if (!in) {
        err = "Unable to open file: " + path.string();
        return false;
    }

    std::string line;
    int lineNo = 0;
    while (std::getline(in, line)) {
        ++lineNo;
        if (line.find_first_not_of(" \t\r\n") == std::string::npos) continue;
        ResultRow row;
        if (!ParseRow(line, row, err)) {
            err = path.string() + ":" + std::to_string(lineNo) + ": " + err;
            return false;
        }
        rows.push_back(std::move(row));
    }

    return true;
}

std::map<std::string, std::vector<ResultRow>> GroupByCase(std::vector<ResultRow> rows) {
    std::map<std::string, std::vector<ResultRow>> grouped;
    for (auto& row : rows) grouped[row.caseId].push_back(std::move(row));
    for (auto& kv : grouped) {
        auto& vec = kv.second;
        std::sort(vec.begin(), vec.end(), [](const ResultRow& a, const ResultRow& b) {
            return a.rank < b.rank;
        });
    }
    return grouped;
}

std::optional<Mismatch> DiffCase(const std::string& caseId,
                                 const std::vector<ResultRow>& admin,
                                 const std::vector<ResultRow>& nonAdmin) {
    if (admin.size() != nonAdmin.size()) {
        return Mismatch{ caseId, "count", std::to_string(admin.size()), std::to_string(nonAdmin.size()), 0, "query" };
    }

    for (size_t i = 0; i < admin.size(); ++i) {
        const auto& a = admin[i];
        const auto& b = nonAdmin[i];
        const bool same =
            a.recordId == b.recordId &&
            a.path == b.path &&
            a.size == b.size &&
            a.modified == b.modified &&
            a.attributes == b.attributes;

        if (!same) {
            const std::string mismatchType = (a.recordId != b.recordId) ? "order" : "metadata";
            const std::string owner = (mismatchType == "order") ? "sort" : "record_mapping";
            return Mismatch{ caseId, mismatchType, a.raw, b.raw, static_cast<int>(i), owner };
        }
    }

    return std::nullopt;
}

std::vector<Mismatch> DiffAll(const std::map<std::string, std::vector<ResultRow>>& groupedAdmin,
                              const std::map<std::string, std::vector<ResultRow>>& groupedNonAdmin,
                              int& outTotalCases) {
    std::vector<std::string> caseIds;
    caseIds.reserve(groupedAdmin.size() + groupedNonAdmin.size());
    for (const auto& kv : groupedAdmin) caseIds.push_back(kv.first);
    for (const auto& kv : groupedNonAdmin) {
        if (!groupedAdmin.count(kv.first)) caseIds.push_back(kv.first);
    }
    std::sort(caseIds.begin(), caseIds.end());
    outTotalCases = static_cast<int>(caseIds.size());

    std::vector<Mismatch> diffs;
    for (const auto& caseId : caseIds) {
        const auto adminIt = groupedAdmin.find(caseId);
        const auto nonAdminIt = groupedNonAdmin.find(caseId);
        const std::vector<ResultRow> empty;
        const auto& a = (adminIt == groupedAdmin.end()) ? empty : adminIt->second;
        const auto& b = (nonAdminIt == groupedNonAdmin.end()) ? empty : nonAdminIt->second;

        if (auto mismatch = DiffCase(caseId, a, b)) {
            diffs.push_back(std::move(*mismatch));
        }
    }
    return diffs;
}

bool WriteDiffJson(const std::filesystem::path& path, const std::vector<Mismatch>& diffs, std::string& err) {
    std::ofstream out(path);
    if (!out) {
        err = "Unable to write diff file: " + path.string();
        return false;
    }

    out << "[\n";
    for (size_t i = 0; i < diffs.size(); ++i) {
        const auto& d = diffs[i];
        out << "  {\n"
            << "    \"case_id\": \"" << EscapeJson(d.caseId) << "\",\n"
            << "    \"mismatch_type\": \"" << EscapeJson(d.mismatchType) << "\",\n"
            << "    \"admin_value\": \"" << EscapeJson(d.adminValue) << "\",\n"
            << "    \"non_admin_value\": \"" << EscapeJson(d.nonAdminValue) << "\",\n"
            << "    \"first_index\": " << d.firstIndex << ",\n"
            << "    \"suggested_owner\": \"" << EscapeJson(d.suggestedOwner) << "\"\n"
            << "  }";
        if (i + 1 < diffs.size()) out << ',';
        out << "\n";
    }
    out << "]\n";
    return true;
}

bool WriteSummaryJson(const std::filesystem::path& path, int totalCases, const std::vector<Mismatch>& diffs, std::string& err) {
    std::ofstream out(path);
    if (!out) {
        err = "Unable to write summary file: " + path.string();
        return false;
    }

    out << "{\n"
        << "  \"pass\": " << (diffs.empty() ? "true" : "false") << ",\n"
        << "  \"total_cases\": " << totalCases << ",\n"
        << "  \"mismatch_count\": " << diffs.size() << ",\n"
        << "  \"first_mismatch\": ";

    if (diffs.empty()) {
        out << "null\n";
    }
    else {
        const auto& d = diffs.front();
        out << "{\"case_id\":\"" << EscapeJson(d.caseId)
            << "\",\"mismatch_type\":\"" << EscapeJson(d.mismatchType)
            << "\",\"first_index\":" << d.firstIndex
            << ",\"suggested_owner\":\"" << EscapeJson(d.suggestedOwner) << "\"}\n";
    }

    out << "}\n";
    return true;
}

} // namespace parity

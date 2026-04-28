#include "ParityComparatorLib.h"

#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    std::string adminPath;
    std::string nonAdminPath;
    std::string diffPath;
    std::string summaryPath;

    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        auto next = [&]() -> std::string {
            if (i + 1 >= argc) return {};
            return argv[++i];
        };

        if (arg == "--admin") adminPath = next();
        else if (arg == "--non-admin") nonAdminPath = next();
        else if (arg == "--diff-out") diffPath = next();
        else if (arg == "--summary-out") summaryPath = next();
    }

    if (adminPath.empty() || nonAdminPath.empty() || diffPath.empty() || summaryPath.empty()) {
        std::cerr << "Usage: parity_comparator --admin <file> --non-admin <file> --diff-out <file> --summary-out <file>\n";
        return 2;
    }

    std::vector<parity::ResultRow> adminRows;
    std::vector<parity::ResultRow> nonAdminRows;
    std::string err;

    if (!parity::ReadJsonl(adminPath, adminRows, err)) {
        std::cerr << err << "\n";
        return 2;
    }
    if (!parity::ReadJsonl(nonAdminPath, nonAdminRows, err)) {
        std::cerr << err << "\n";
        return 2;
    }

    auto groupedAdmin = parity::GroupByCase(std::move(adminRows));
    auto groupedNonAdmin = parity::GroupByCase(std::move(nonAdminRows));
    int totalCases = 0;
    auto diffs = parity::DiffAll(groupedAdmin, groupedNonAdmin, totalCases);

    std::filesystem::create_directories(std::filesystem::path(diffPath).parent_path());
    std::filesystem::create_directories(std::filesystem::path(summaryPath).parent_path());

    if (!parity::WriteDiffJson(diffPath, diffs, err)) {
        std::cerr << err << "\n";
        return 2;
    }
    if (!parity::WriteSummaryJson(summaryPath, totalCases, diffs, err)) {
        std::cerr << err << "\n";
        return 2;
    }

    return diffs.empty() ? 0 : 1;
}

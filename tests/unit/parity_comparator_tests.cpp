#include "../parity/ParityComparatorLib.h"

#include <filesystem>
#include <fstream>
#include <iostream>
#include <vector>

static bool Check(bool cond, const char* msg) {
    if (!cond) {
        std::cerr << "[FAIL] " << msg << "\n";
        return false;
    }
    return true;
}

int main() {
    bool ok = true;

    {
        parity::ResultRow row;
        std::string err;
        const std::string line = R"({"case_id":"c1","rank":0,"record_id":42,"path":"C:\\a.txt","size":10,"modified":11,"attributes":32})";
        ok &= Check(parity::ParseRow(line, row, err), "ParseRow should parse valid row.");
        ok &= Check(row.caseId == "c1", "case_id parsed.");
        ok &= Check(row.recordId == 42, "record_id parsed.");
    }

    {
        std::vector<parity::ResultRow> a{
            {"c1", 0, 1, "A", 1, 1, 32, "a"},
            {"c1", 1, 2, "B", 1, 1, 32, "b"}
        };
        std::vector<parity::ResultRow> b{
            {"c1", 0, 1, "A", 1, 1, 32, "a"},
            {"c1", 1, 2, "B", 1, 1, 32, "b"}
        };

        auto diff = parity::DiffCase("c1", a, b);
        ok &= Check(!diff.has_value(), "DiffCase should return no mismatch for identical rows.");
    }

    {
        std::vector<parity::ResultRow> a{{"c1", 0, 1, "A", 1, 1, 32, "a"}};
        std::vector<parity::ResultRow> b{{"c1", 0, 2, "B", 1, 1, 32, "b"}};

        auto diff = parity::DiffCase("c1", a, b);
        ok &= Check(diff.has_value(), "DiffCase should detect mismatch.");
        if (diff) {
            ok &= Check(diff->mismatchType == "order", "Mismatch type should be order when record_id differs.");
            ok &= Check(diff->firstIndex == 0, "First mismatch index should be 0.");
        }
    }

    {
        // End-to-end JSONL read + write smoke.
        auto temp = std::filesystem::temp_directory_path() / "whereisit_parity_test";
        std::filesystem::create_directories(temp);
        const auto admin = temp / "admin.jsonl";
        const auto nonadmin = temp / "nonadmin.jsonl";
        const auto diffOut = temp / "diff.json";
        const auto summaryOut = temp / "summary.json";

        std::ofstream(admin) << R"({"case_id":"c1","rank":0,"record_id":1,"path":"A","size":1,"modified":1,"attributes":1})" << "\n";
        std::ofstream(nonadmin) << R"({"case_id":"c1","rank":0,"record_id":1,"path":"A","size":1,"modified":1,"attributes":1})" << "\n";

        std::vector<parity::ResultRow> aRows;
        std::vector<parity::ResultRow> bRows;
        std::string err;
        ok &= Check(parity::ReadJsonl(admin, aRows, err), "ReadJsonl admin should succeed.");
        ok &= Check(parity::ReadJsonl(nonadmin, bRows, err), "ReadJsonl nonadmin should succeed.");

        int total = 0;
        auto diffs = parity::DiffAll(parity::GroupByCase(std::move(aRows)), parity::GroupByCase(std::move(bRows)), total);
        ok &= Check(total == 1, "DiffAll total case count should be 1.");
        ok &= Check(diffs.empty(), "DiffAll should have no diffs for matching files.");
        ok &= Check(parity::WriteDiffJson(diffOut, diffs, err), "WriteDiffJson should succeed.");
        ok &= Check(parity::WriteSummaryJson(summaryOut, total, diffs, err), "WriteSummaryJson should succeed.");
    }

    if (!ok) return 1;
    std::cout << "[PASS] parity_comparator_tests\n";
    return 0;
}

#include "../../SortService.h"

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
        std::vector<sortservice::SortRecord> rows = {
            {2, L"beta", L"", 0, 0},
            {1, L"alpha", L"", 0, 0},
            {3, L"alpha", L"", 0, 0}
        };
        sortservice::SortRecords(rows, QuerySortKey::Name, false);
        ok &= Check(rows[0].idx == 1 && rows[1].idx == 3 && rows[2].idx == 2, "Name ascending should be stable + tie-break by idx.");
    }

    {
        std::vector<uint32_t> indices = {1, 2, 3};
        bool canceled = false;
        const bool result = sortservice::BuildAndSortRecords(
            indices,
            QuerySortKey::Size,
            true,
            [](uint32_t idx, sortservice::SortRecord& out) {
                out.idx = idx;
                out.name = (idx == 1 ? L"a" : idx == 2 ? L"b" : L"c");
                out.size = (idx == 1 ? 10 : idx == 2 ? 30 : 20);
                return true;
            },
            [&canceled]() {
                return canceled;
            });

        ok &= Check(result, "BuildAndSortRecords should complete when not canceled.");
        ok &= Check(indices[0] == 2 && indices[1] == 3 && indices[2] == 1, "Size descending order should match expected.");
    }

    {
        std::vector<uint32_t> indices(400, 1);
        bool first = true;
        const bool result = sortservice::BuildAndSortRecords(
            indices,
            QuerySortKey::Name,
            false,
            [](uint32_t idx, sortservice::SortRecord& out) {
                out.idx = idx;
                out.name = L"x";
                return true;
            },
            [&first]() {
                if (first) {
                    first = false;
                    return false;
                }
                return true;
            });

        ok &= Check(!result, "BuildAndSortRecords should abort when cancellation is requested.");
    }

    if (!ok) return 1;
    std::cout << "[PASS] sort_service_tests\n";
    return 0;
}

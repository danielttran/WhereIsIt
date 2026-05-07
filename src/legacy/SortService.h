#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "CoreTypes.h"

namespace sortservice {

struct SortRecord {
    uint32_t idx = 0;
    std::wstring name;
    std::wstring parentPath;
    uint64_t size = 0;
    uint64_t date = 0;
};

using FillRecordFn = std::function<bool(uint32_t, SortRecord&)>;
using CancelFn = std::function<bool()>;

void SortRecords(std::vector<SortRecord>& rows, QuerySortKey key, bool descending);

// Builds sort keys from record indices, sorts, and writes the new index order back.
// Returns false when canceled before completion.
bool BuildAndSortRecords(std::vector<uint32_t>& indices,
                         QuerySortKey key,
                         bool descending,
                         const FillRecordFn& fillRecord,
                         const CancelFn& shouldCancel);

} // namespace sortservice

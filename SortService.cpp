#include "SortService.h"

#include <algorithm>
#include <cwctype>

namespace sortservice {

namespace {

int CompareNoCase(const std::wstring& a, const std::wstring& b)
{
    const size_t n = (a.size() < b.size()) ? a.size() : b.size();
    for (size_t i = 0; i < n; ++i) {
        const wchar_t ca = (wchar_t)towlower(a[i]);
        const wchar_t cb = (wchar_t)towlower(b[i]);
        if (ca < cb) return -1;
        if (ca > cb) return 1;
    }
    if (a.size() < b.size()) return -1;
    if (a.size() > b.size()) return 1;
    return 0;
}

bool CompareByKey(const SortRecord& a, const SortRecord& b, QuerySortKey key, bool descending)
{
    int cmp = 0;
    if (key == QuerySortKey::Path) {
        cmp = CompareNoCase(a.parentPath, b.parentPath);
        if (cmp == 0) cmp = CompareNoCase(a.name, b.name);
    }
    else if (key == QuerySortKey::Size) {
        if (a.size < b.size) cmp = -1;
        else if (a.size > b.size) cmp = 1;
        if (cmp == 0) cmp = CompareNoCase(a.name, b.name);
    }
    else if (key == QuerySortKey::Date) {
        if (a.date < b.date) cmp = -1;
        else if (a.date > b.date) cmp = 1;
        if (cmp == 0) cmp = CompareNoCase(a.name, b.name);
    }
    else {
        cmp = CompareNoCase(a.name, b.name);
    }

    if (cmp == 0) cmp = (a.idx < b.idx) ? -1 : 1;
    return descending ? (cmp > 0) : (cmp < 0);
}

} // namespace

void SortRecords(std::vector<SortRecord>& rows, QuerySortKey key, bool descending)
{
    std::stable_sort(rows.begin(), rows.end(), [key, descending](const SortRecord& a, const SortRecord& b) {
        return CompareByKey(a, b, key, descending);
    });
}

bool BuildAndSortRecords(std::vector<uint32_t>& indices,
                         QuerySortKey key,
                         bool descending,
                         const FillRecordFn& fillRecord,
                         const CancelFn& shouldCancel)
{
    if (indices.size() < 2) return true;

    std::vector<SortRecord> rows;
    rows.reserve(indices.size());

    size_t n = 0;
    for (uint32_t idx : indices) {
        if ((n++ & 0xFF) == 0 && shouldCancel && shouldCancel()) return false;
        SortRecord rec;
        rec.idx = idx;
        if (!fillRecord(idx, rec)) return false;
        rows.push_back(std::move(rec));
    }

    if (shouldCancel && shouldCancel()) return false;

    SortRecords(rows, key, descending);
    for (size_t i = 0; i < rows.size(); ++i) indices[i] = rows[i].idx;
    return true;
}

} // namespace sortservice

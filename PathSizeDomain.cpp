#include "PathSizeDomain.h"


namespace pathsize {

uint64_t ResolveFileSizeFromRecord(const FileRecord& rec, bool hasMappedGiantSize, uint64_t mappedGiantSize) noexcept {
    if (rec.IsGiantFile) {
        if (hasMappedGiantSize) return mappedGiantSize;
        return kUnknownGiantSizeLowerBound;
    }
    return rec.FileSize;
}

std::wstring JoinParentAndName(const std::wstring& parent, const std::wstring& name) {
    if (parent.empty()) return name;
    if (name.empty()) return parent;
    if (parent.back() == L'\\' || parent.back() == L'/') return parent + name;
    return parent + L"\\" + name;
}

} // namespace pathsize

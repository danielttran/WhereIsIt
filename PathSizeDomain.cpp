#include "PathSizeDomain.h"

namespace pathsize {

uint64_t ResolveFileSizeFromRecord(const FileRecord& rec, bool hasMappedGiantSize, uint64_t mappedGiantSize) noexcept {
    if (rec.IsGiantFile) {
        if (hasMappedGiantSize) return mappedGiantSize;
        return kUnknownGiantSizeLowerBound;
    }
    return rec.FileSize;
}

} // namespace pathsize

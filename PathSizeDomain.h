#pragma once

#include <cstdint>
#include "CoreTypes.h"

namespace pathsize {

inline constexpr uint64_t kUnknownGiantSizeLowerBound = static_cast<uint64_t>(kGiantFileMarker);

uint64_t ResolveFileSizeFromRecord(const FileRecord& rec, bool hasMappedGiantSize, uint64_t mappedGiantSize) noexcept;

} // namespace pathsize

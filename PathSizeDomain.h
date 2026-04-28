#pragma once

#include <cstdint>
#include <string>
#include "CoreTypes.h"

namespace pathsize {

inline constexpr uint64_t kUnknownGiantSizeLowerBound = static_cast<uint64_t>(kGiantFileMarker);

uint64_t ResolveFileSizeFromRecord(const FileRecord& rec, bool hasMappedGiantSize, uint64_t mappedGiantSize) noexcept;
std::wstring JoinParentAndName(const std::wstring& parent, const std::wstring& name);

} // namespace pathsize

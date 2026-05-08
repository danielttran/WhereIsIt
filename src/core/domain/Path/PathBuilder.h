#pragma once

#include <functional>
#include <string>

namespace whereisit::core::path {

using RecordLookup = std::function<bool(unsigned, unsigned&, std::wstring&)>;
std::wstring BuildFullPath(unsigned index, const RecordLookup& lookup, unsigned maxDepth = 1024);

}

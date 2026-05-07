#include "PathBuilder.h"

#include <vector>

namespace whereisit::core::path {

std::wstring BuildFullPath(unsigned index, const RecordLookup& lookup, unsigned maxDepth) {
    std::vector<std::wstring> parts;
    unsigned cur = index;
    unsigned depth = 0;
    while (depth++ < maxDepth) {
        unsigned parent = cur;
        std::wstring name;
        if (!lookup(cur, parent, name)) break;
        if (!name.empty()) parts.push_back(name);
        if (parent == cur) break;
        cur = parent;
    }

    std::wstring full;
    for (auto it = parts.rbegin(); it != parts.rend(); ++it) {
        if (!full.empty() && full.back() != L'\\') full += L"\\";
        full += *it;
    }
    return full;
}

}

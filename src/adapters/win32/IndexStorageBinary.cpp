#include "IndexStorageBinary.h"

#include <fstream>

namespace whereisit::adapters::win32 {

bool IndexStorageBinary::Save(const std::wstring& path, const whereisit::core::ports::IndexSnapshot& snapshot) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out) return false;
    const std::uint64_t count = snapshot.records.size();
    out.write(reinterpret_cast<const char*>(&count), sizeof(count));
    return out.good();
}

bool IndexStorageBinary::Load(const std::wstring& path, whereisit::core::ports::IndexSnapshot& outSnapshot) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return false;
    std::uint64_t count = 0;
    in.read(reinterpret_cast<char*>(&count), sizeof(count));
    if (!in.good()) return false;
    outSnapshot.records.clear();
    outSnapshot.records.reserve(static_cast<size_t>(count));
    return true;
}

}

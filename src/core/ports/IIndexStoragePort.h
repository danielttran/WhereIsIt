#pragma once

#include <string>
#include <vector>

#include "IFileSystemScannerPort.h"

namespace whereisit::core::ports {

struct IndexSnapshot {
    std::vector<ScanRecord> records;
};

class IIndexStoragePort {
public:
    virtual ~IIndexStoragePort() = default;
    virtual bool Save(const std::wstring& path, const IndexSnapshot& snapshot) = 0;
    virtual bool Load(const std::wstring& path, IndexSnapshot& outSnapshot) = 0;
};

} // namespace whereisit::core::ports

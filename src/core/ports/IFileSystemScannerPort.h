#pragma once

#include <functional>
#include <string>

namespace whereisit::core::ports {

struct ScanRecord {
    unsigned mftIndex = 0;
    unsigned parentIndex = 0;
    unsigned long long size = 0;
    std::wstring name;
};

class IFileSystemScannerPort {
public:
    virtual ~IFileSystemScannerPort() = default;
    virtual bool ScanDrive(const std::wstring& driveRoot,
                           const std::function<void(const ScanRecord&)>& onRecord) = 0;
};

} // namespace whereisit::core::ports

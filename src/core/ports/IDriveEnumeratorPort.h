#pragma once

#include <string>
#include <vector>

namespace whereisit::core::ports {

struct DriveInfo {
    std::wstring root;
    bool isNtfs = false;
};

class IDriveEnumeratorPort {
public:
    virtual ~IDriveEnumeratorPort() = default;
    virtual std::vector<DriveInfo> EnumerateFixedDrives() const = 0;
};

} // namespace whereisit::core::ports

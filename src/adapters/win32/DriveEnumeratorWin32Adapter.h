#pragma once

#include <memory>
#include <vector>

#include "../../core/ports/IDriveEnumeratorPort.h"
#include "../../engine/native/cpp/DriveEnumeratorWin32.h"

namespace whereisit::adapters::win32 {

class DriveEnumeratorWin32Adapter final : public whereisit::core::ports::IDriveEnumeratorPort {
public:
    explicit DriveEnumeratorWin32Adapter(std::unique_ptr<IDriveEnumerator> impl = std::make_unique<DriveEnumeratorWin32>());
    std::vector<whereisit::core::ports::DriveInfo> EnumerateFixedDrives() const override;

private:
    std::unique_ptr<IDriveEnumerator> impl_;
};

} // namespace whereisit::adapters::win32

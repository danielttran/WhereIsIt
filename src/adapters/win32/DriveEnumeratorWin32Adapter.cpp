#include "DriveEnumeratorWin32Adapter.h"

namespace whereisit::adapters::win32 {

DriveEnumeratorWin32Adapter::DriveEnumeratorWin32Adapter(std::unique_ptr<IDriveEnumerator> impl)
    : impl_(std::move(impl)) {}

std::vector<whereisit::core::ports::DriveInfo> DriveEnumeratorWin32Adapter::EnumerateFixedDrives() const {
    std::vector<whereisit::core::ports::DriveInfo> result;
    if (!impl_) return result;

    const auto drives = impl_->EnumerateDriveRoots();
    result.reserve(drives.size());
    for (const auto& drive : drives) {
        result.push_back({drive, true});
    }
    return result;
}

} // namespace whereisit::adapters::win32

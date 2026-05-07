#pragma once

#include "IDriveEnumerator.h"

class DriveEnumeratorWin32 final : public IDriveEnumerator {
public:
    std::vector<std::wstring> EnumerateDriveRoots() const override;
};

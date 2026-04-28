#pragma once

#include <string>
#include <vector>

class IDriveEnumerator {
public:
    virtual ~IDriveEnumerator() = default;
    virtual std::vector<std::wstring> EnumerateDriveRoots() const = 0;
};

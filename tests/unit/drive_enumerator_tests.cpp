#include <iostream>

#include "../../DriveEnumeratorWin32.h"

int main() {
    DriveEnumeratorWin32 enumerator;
    auto drives = enumerator.EnumerateDriveRoots();
#ifndef _WIN32
    if (!drives.empty()) {
        std::cerr << "[FAIL] non-Windows fallback should return empty\n";
        return 1;
    }
#endif
    std::cout << "[PASS] drive_enumerator_tests\n";
    return 0;
}

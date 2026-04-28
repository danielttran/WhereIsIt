#include "DriveEnumeratorWin32.h"

#ifdef _WIN32
#include <windows.h>

#include <cwchar>

std::vector<std::wstring> DriveEnumeratorWin32::EnumerateDriveRoots() const {
    std::vector<std::wstring> roots;

    wchar_t buf[2048] = {0};
    DWORD len = GetLogicalDriveStringsW(static_cast<DWORD>(std::size(buf)), buf);
    if (len == 0 || len >= std::size(buf)) return roots;

    for (const wchar_t* p = buf; *p; p += (wcslen(p) + 1)) {
        roots.emplace_back(p);
    }

    return roots;
}

#else

std::vector<std::wstring> DriveEnumeratorWin32::EnumerateDriveRoots() const {
    return {};
}

#endif

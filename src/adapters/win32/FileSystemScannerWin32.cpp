#include "FileSystemScannerWin32.h"

namespace whereisit::adapters::win32 {

bool FileSystemScannerWin32::ScanDrive(const std::wstring& driveRoot,
                                       const std::function<void(const whereisit::core::ports::ScanRecord&)>& onRecord) {
    if (driveRoot.empty()) return false;
    if (onRecord) {
        onRecord({0, 0, 0, L""});
    }
    return true;
}

}

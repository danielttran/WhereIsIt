#pragma once

#include "../../core/ports/IFileSystemScannerPort.h"

namespace whereisit::adapters::win32 {

class FileSystemScannerWin32 final : public whereisit::core::ports::IFileSystemScannerPort {
public:
    bool ScanDrive(const std::wstring& driveRoot,
                   const std::function<void(const whereisit::core::ports::ScanRecord&)>& onRecord) override;
};

}

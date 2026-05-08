#pragma once

#include "../../core/ports/IIndexStoragePort.h"

namespace whereisit::adapters::win32 {

class IndexStorageBinary final : public whereisit::core::ports::IIndexStoragePort {
public:
    bool Save(const std::wstring& path, const whereisit::core::ports::IndexSnapshot& snapshot) override;
    bool Load(const std::wstring& path, whereisit::core::ports::IndexSnapshot& outSnapshot) override;
};

}

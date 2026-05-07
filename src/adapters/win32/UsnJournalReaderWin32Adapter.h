#pragma once

#include <memory>

#include "../../core/ports/IUsnJournalReaderPort.h"
#include "../../legacy/UsnJournalReaderWin32.h"

namespace whereisit::adapters::win32 {

class UsnJournalReaderWin32Adapter final : public whereisit::core::ports::IUsnJournalReaderPort {
public:
    explicit UsnJournalReaderWin32Adapter(std::unique_ptr<IUsnJournalReader> impl = std::make_unique<UsnJournalReaderWin32>());
    bool ReadDeltaBatch(void* volumeHandle,
                        uint64_t startUsn,
                        std::vector<whereisit::core::ports::UsnRecordView>& outRecords,
                        uint64_t& outNextUsn) const override;

private:
    std::unique_ptr<IUsnJournalReader> impl_;
};

} // namespace whereisit::adapters::win32

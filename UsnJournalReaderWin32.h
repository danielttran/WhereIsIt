#pragma once

#include "IUsnJournalReader.h"

class UsnJournalReaderWin32 final : public IUsnJournalReader {
public:
    bool ReadDeltaBatch(void* volumeHandle,
                        uint64_t startUsn,
                        std::vector<UsnRecordView>& outRecords,
                        uint64_t& outNextUsn) const override;
};

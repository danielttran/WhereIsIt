#pragma once

#include <cstdint>
#include <vector>

struct UsnRecordView {
    uint32_t fileReferenceNumber = 0;
    uint32_t parentFileReferenceNumber = 0;
    uint32_t reasonMask = 0;
    uint64_t nextUsn = 0;
};

class IUsnJournalReader {
public:
    virtual ~IUsnJournalReader() = default;
    virtual bool ReadDeltaBatch(void* volumeHandle,
                                uint64_t startUsn,
                                std::vector<UsnRecordView>& outRecords,
                                uint64_t& outNextUsn) const = 0;
};

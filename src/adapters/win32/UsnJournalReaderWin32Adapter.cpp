#include "UsnJournalReaderWin32Adapter.h"

namespace whereisit::adapters::win32 {

UsnJournalReaderWin32Adapter::UsnJournalReaderWin32Adapter(std::unique_ptr<IUsnJournalReader> impl)
    : impl_(std::move(impl)) {}

bool UsnJournalReaderWin32Adapter::ReadDeltaBatch(void* volumeHandle,
                                                  uint64_t startUsn,
                                                  std::vector<whereisit::core::ports::UsnRecordView>& outRecords,
                                                  uint64_t& outNextUsn) const {
    if (!impl_) return false;

    std::vector<::UsnRecordView> legacyRecords;
    const bool ok = impl_->ReadDeltaBatch(volumeHandle, startUsn, legacyRecords, outNextUsn);
    if (!ok) return false;

    outRecords.clear();
    outRecords.reserve(legacyRecords.size());
    for (const auto& row : legacyRecords) {
        outRecords.push_back({row.fileReferenceNumber, row.parentFileReferenceNumber, row.reasonMask, row.nextUsn});
    }
    return true;
}

} // namespace whereisit::adapters::win32

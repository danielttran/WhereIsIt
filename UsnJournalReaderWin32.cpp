#include "UsnJournalReaderWin32.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winioctl.h>

bool UsnJournalReaderWin32::ReadDeltaBatch(void* volumeHandle,
                                           uint64_t startUsn,
                                           std::vector<UsnRecordView>& outRecords,
                                           uint64_t& outNextUsn) const {
    outRecords.clear();
    outNextUsn = startUsn;

    if (volumeHandle == nullptr || volumeHandle == INVALID_HANDLE_VALUE) return false;
    HANDLE h = static_cast<HANDLE>(volumeHandle);

    USN_JOURNAL_DATA_V0 uj{};
    DWORD cb = 0;
    if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, nullptr, 0, &uj, sizeof(uj), &cb, nullptr)) return false;

    std::vector<uint8_t> buffer(65536);
    READ_USN_JOURNAL_DATA_V0 rd = { static_cast<USN>(startUsn), 0, FALSE, 0, 0, uj.UsnJournalID };
    if (!DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, &rd, sizeof(rd), buffer.data(), static_cast<DWORD>(buffer.size()), &cb, nullptr)) return false;

    if (cb <= sizeof(USN)) return true;

    uint8_t* p = buffer.data() + sizeof(USN);
    uint8_t* e = buffer.data() + cb;
    while (p < e) {
        auto* r = reinterpret_cast<USN_RECORD_V2*>(p);
        UsnRecordView view{};
        view.fileReferenceNumber = static_cast<uint32_t>(r->FileReferenceNumber & 0xffffffffu);
        view.parentFileReferenceNumber = static_cast<uint32_t>(r->ParentFileReferenceNumber & 0xffffffffu);
        view.reasonMask = r->Reason;
        view.nextUsn = static_cast<uint64_t>(r->Usn);
        outRecords.push_back(view);
        p += r->RecordLength;
    }

    outNextUsn = static_cast<uint64_t>(*reinterpret_cast<USN*>(buffer.data()));
    return true;
}

#else

bool UsnJournalReaderWin32::ReadDeltaBatch(void*, uint64_t startUsn, std::vector<UsnRecordView>& outRecords, uint64_t& outNextUsn) const {
    outRecords.clear();
    outNextUsn = startUsn;
    return false;
}

#endif

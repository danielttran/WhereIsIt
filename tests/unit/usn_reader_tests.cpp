#include <iostream>
#include <vector>

#include "../../UsnJournalReaderWin32.h"

int main() {
    UsnJournalReaderWin32 reader;
    std::vector<UsnRecordView> records;
    uint64_t nextUsn = 123;
    bool ok = reader.ReadDeltaBatch(nullptr, 123, records, nextUsn);
#ifndef _WIN32
    if (ok) {
        std::cerr << "[FAIL] non-Windows fallback should return false\n";
        return 1;
    }
#endif
    if (!records.empty()) {
        std::cerr << "[FAIL] expected no records for null handle\n";
        return 1;
    }
    if (nextUsn != 123) {
        std::cerr << "[FAIL] nextUsn should remain unchanged on fallback/failure\n";
        return 1;
    }
    std::cout << "[PASS] usn_reader_tests\n";
    return 0;
}

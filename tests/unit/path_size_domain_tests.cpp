#include <iostream>
#include <string>

#include "../../CoreTypes.h"
#include "../../PathSizeDomain.h"

static int fails = 0;

void Check(bool cond, const char* msg) {
    if (!cond) {
        ++fails;
        std::cerr << "[FAIL] " << msg << "\n";
    }
}

int main() {
    FileRecord normal{};
    normal.FileSize = 123;
    normal.IsGiantFile = 0;
    Check(pathsize::ResolveFileSizeFromRecord(normal, false, 0) == 123, "normal size should be direct");

    FileRecord giant{};
    giant.FileSize = kGiantFileMarker;
    giant.IsGiantFile = 1;
    Check(pathsize::ResolveFileSizeFromRecord(giant, true, 9999999999ull) == 9999999999ull, "giant mapped size should win");
    Check(pathsize::ResolveFileSizeFromRecord(giant, false, 0) == static_cast<uint64_t>(kGiantFileMarker), "giant fallback marker should be returned");

    Check(pathsize::JoinParentAndName(L"", L"name.txt") == L"name.txt", "empty parent should return name");
    Check(pathsize::JoinParentAndName(L"C:\\", L"name.txt") == L"C:\\name.txt", "root parent keeps single slash");
    Check(pathsize::JoinParentAndName(L"C:\\path", L"name.txt") == L"C:\\path\\name.txt", "separator inserted");
    Check(pathsize::JoinParentAndName(L"/var/tmp/", L"file.log") == L"/var/tmp/file.log", "forward slash parent should not double separator");

    if (fails) return 1;
    std::cout << "[PASS] path_size_domain_tests\n";
    return 0;
}

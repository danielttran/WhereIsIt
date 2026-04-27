#include "framework.h"
#include "StringUtils.h"
#include <windows.h>

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return std::wstring();
    int size = MultiByteToWideChar(CP_UTF8, 0, value.data(), (int)value.size(), NULL, 0);
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), (int)value.size(), &result[0], size);
    return result;
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return std::string();
    int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), (int)value.size(), NULL, 0, NULL, NULL);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), (int)value.size(), &result[0], size, NULL, NULL);
    return result;
}

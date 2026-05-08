#include "StructuredLogger.h"

#include <fstream>
#include <iomanip>
#include <sstream>

namespace whereisit::core::logging {

StructuredLogger::StructuredLogger(std::filesystem::path filePath)
    : filePath_(std::move(filePath)) {}

void StructuredLogger::Log(LogLevel level,
                           std::wstring_view operation,
                           std::wstring_view message,
                           std::optional<std::wstring_view> correlationId) {
    const auto now = std::chrono::system_clock::now();
    const std::wstring ts = ToIso8601Utc(now);

    std::wstringstream line;
    line << L"{\"ts\":\"" << EscapeJson(ts)
         << L"\",\"level\":\"" << EscapeJson(LevelToString(level))
         << L"\",\"op\":\"" << EscapeJson(operation)
         << L"\",\"message\":\"" << EscapeJson(message) << L"\"";

    if (correlationId.has_value() && !correlationId->empty()) {
        line << L",\"correlationId\":\"" << EscapeJson(*correlationId) << L"\"";
    }
    line << L"}";

    std::lock_guard<std::mutex> guard(writeMutex_);
    std::wofstream out(filePath_, std::ios::app);
    if (!out) {
        return;
    }
    out << line.str() << L"\n";
}

std::wstring StructuredLogger::ToIso8601Utc(std::chrono::system_clock::time_point tp) const {
    const auto timeT = std::chrono::system_clock::to_time_t(tp);
    std::tm utc{};
#if defined(_WIN32)
    gmtime_s(&utc, &timeT);
#else
    gmtime_r(&timeT, &utc);
#endif

    std::wstringstream ss;
    ss << std::put_time(&utc, L"%Y-%m-%dT%H:%M:%SZ");
    return ss.str();
}

std::wstring StructuredLogger::EscapeJson(std::wstring_view value) {
    std::wstring out;
    out.reserve(value.size() + 8);
    for (wchar_t ch : value) {
        switch (ch) {
            case L'\\': out += L"\\\\"; break;
            case L'"': out += L"\\\""; break;
            case L'\n': out += L"\\n"; break;
            case L'\r': out += L"\\r"; break;
            case L'\t': out += L"\\t"; break;
            default: out += ch; break;
        }
    }
    return out;
}

std::wstring StructuredLogger::LevelToString(LogLevel level) {
    switch (level) {
        case LogLevel::Debug: return L"debug";
        case LogLevel::Info: return L"info";
        case LogLevel::Warning: return L"warning";
        case LogLevel::Error: return L"error";
        default: return L"unknown";
    }
}

} // namespace whereisit::core::logging

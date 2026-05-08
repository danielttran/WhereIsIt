#pragma once

#include <chrono>
#include <filesystem>
#include <mutex>
#include <optional>
#include <string>

namespace whereisit::core::logging {

enum class LogLevel { Debug, Info, Warning, Error };

class StructuredLogger {
public:
    explicit StructuredLogger(std::filesystem::path filePath);

    void Log(LogLevel level,
             std::wstring_view operation,
             std::wstring_view message,
             std::optional<std::wstring_view> correlationId = std::nullopt);

private:
    std::wstring ToIso8601Utc(std::chrono::system_clock::time_point tp) const;
    static std::wstring EscapeJson(std::wstring_view value);
    static std::wstring LevelToString(LogLevel level);

    std::filesystem::path filePath_;
    mutable std::mutex writeMutex_;
};

} // namespace whereisit::core::logging

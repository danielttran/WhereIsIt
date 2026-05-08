#pragma once

#include <filesystem>
#include <optional>
#include <string>

namespace whereisit::core::logging {

struct EventContext {
    std::wstring corr;
    std::wstring module;
    std::wstring event;
    std::wstring level;
    std::optional<std::wstring> op;
    std::optional<unsigned long long> bytes;
    std::optional<unsigned long long> durMs;
    std::optional<unsigned> clientPid;
};

class JsonlEventLogger {
public:
    explicit JsonlEventLogger(std::filesystem::path baseDirectory);
    void Log(const EventContext& ctx, std::optional<std::wstring> query = std::nullopt);
    void PurgeOlderThanDays(int retentionDays);

private:
    std::filesystem::path ResolveDailyPath() const;
    static std::wstring Hash12(std::wstring_view text);
    static std::wstring Escape(std::wstring_view value);

    std::filesystem::path baseDirectory_;
};

} // namespace whereisit::core::logging

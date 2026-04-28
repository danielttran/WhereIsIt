#pragma once

#include <string>
#include <string_view>

// Phase 0 logging seam:
// - ILogger allows the refactor to inject structured logging without coupling
//   domain logic to Win32 or any concrete sink.
// - NullLogger is the default no-op implementation to preserve current behavior.
enum class LogLevel {
    Debug,
    Info,
    Warn,
    Error,
};

struct LogMessage {
    LogLevel Level = LogLevel::Info;
    std::wstring_view Module;
    std::wstring_view Event;
    std::wstring_view CorrelationId;
    std::wstring Message;
};

class ILogger {
public:
    virtual ~ILogger() = default;
    virtual void Write(const LogMessage& msg) = 0;
};

class NullLogger final : public ILogger {
public:
    void Write(const LogMessage& /*msg*/) override {}
};

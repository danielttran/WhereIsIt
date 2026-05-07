#pragma once

#include <string>

namespace whereisit::core::ports {

class ILoggerPort {
public:
    virtual ~ILoggerPort() = default;
    virtual void LogEvent(const std::wstring& level,
                          const std::wstring& event,
                          const std::wstring& module,
                          const std::wstring& corr) = 0;
};

} // namespace whereisit::core::ports

#pragma once

#include <filesystem>

#include "../../core/logging/StructuredLogger.h"
#include "../../core/ports/ILoggerPort.h"

namespace whereisit::adapters::win32 {

class JsonlFileLogger final : public whereisit::core::ports::ILoggerPort {
public:
    explicit JsonlFileLogger(std::filesystem::path path);
    void LogEvent(const std::wstring& level,
                  const std::wstring& event,
                  const std::wstring& module,
                  const std::wstring& corr) override;

private:
    whereisit::core::logging::StructuredLogger logger_;
};

}

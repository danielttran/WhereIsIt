#include "JsonlFileLogger.h"

namespace whereisit::adapters::win32 {

JsonlFileLogger::JsonlFileLogger(std::filesystem::path path)
    : logger_(std::move(path)) {}

void JsonlFileLogger::LogEvent(const std::wstring& level,
                               const std::wstring& event,
                               const std::wstring& module,
                               const std::wstring& corr) {
    whereisit::core::logging::LogLevel mapped = whereisit::core::logging::LogLevel::Info;
    if (level == L"debug") mapped = whereisit::core::logging::LogLevel::Debug;
    else if (level == L"warning") mapped = whereisit::core::logging::LogLevel::Warning;
    else if (level == L"error") mapped = whereisit::core::logging::LogLevel::Error;

    logger_.Log(mapped, module + L":" + event, event, corr);
}

}

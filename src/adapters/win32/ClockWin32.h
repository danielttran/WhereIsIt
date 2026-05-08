#pragma once

#include "../../core/ports/IClockPort.h"

namespace whereisit::adapters::win32 {

class ClockWin32 final : public whereisit::core::ports::IClockPort {
public:
    std::uint64_t UtcNowUnixMs() const override;
};

}

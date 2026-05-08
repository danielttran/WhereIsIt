#pragma once

#include <cstdint>

namespace whereisit::core::ports {

class IClockPort {
public:
    virtual ~IClockPort() = default;
    virtual std::uint64_t UtcNowUnixMs() const = 0;
};

} // namespace whereisit::core::ports

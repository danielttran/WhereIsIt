#pragma once

namespace whereisit::core::ports {

class IEventSignalPort {
public:
    virtual ~IEventSignalPort() = default;
    virtual void Set() = 0;
    virtual void Reset() = 0;
    virtual bool Wait(unsigned timeoutMs) = 0;
};

} // namespace whereisit::core::ports

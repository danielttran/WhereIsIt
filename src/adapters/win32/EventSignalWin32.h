#pragma once

#include <condition_variable>
#include <mutex>

#include "../../core/ports/IEventSignalPort.h"

namespace whereisit::adapters::win32 {

class EventSignalWin32 final : public whereisit::core::ports::IEventSignalPort {
public:
    void Set() override;
    void Reset() override;
    bool Wait(unsigned timeoutMs) override;

private:
    std::mutex mutex_;
    std::condition_variable cv_;
    bool signaled_ = false;
};

}

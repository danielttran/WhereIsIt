#include "EventSignalWin32.h"

#include <chrono>

namespace whereisit::adapters::win32 {

void EventSignalWin32::Set() {
    std::lock_guard<std::mutex> lock(mutex_);
    signaled_ = true;
    cv_.notify_all();
}

void EventSignalWin32::Reset() {
    std::lock_guard<std::mutex> lock(mutex_);
    signaled_ = false;
}

bool EventSignalWin32::Wait(unsigned timeoutMs) {
    std::unique_lock<std::mutex> lock(mutex_);
    return cv_.wait_for(lock, std::chrono::milliseconds(timeoutMs), [this] { return signaled_; });
}

}

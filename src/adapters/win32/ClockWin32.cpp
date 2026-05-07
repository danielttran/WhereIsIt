#include "ClockWin32.h"

#include <chrono>

namespace whereisit::adapters::win32 {

std::uint64_t ClockWin32::UtcNowUnixMs() const {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

}

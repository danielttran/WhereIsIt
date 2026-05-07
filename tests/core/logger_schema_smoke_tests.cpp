#include "../../src/core/logging/JsonlEventLogger.h"

int main() {
    whereisit::core::logging::JsonlEventLogger logger(L"./artifacts/logs");
    whereisit::core::logging::EventContext ctx{L"corr-1", L"ipc", L"ipc.request.recv", L"info", L"search", 128ull, 5ull, 123u};
    logger.Log(ctx, L"my query");
    logger.PurgeOlderThanDays(30);
    return 0;
}

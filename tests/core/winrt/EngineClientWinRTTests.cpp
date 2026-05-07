#include "../../../src/engine/winrt/EngineClient.h"

#include <cassert>

int main() {
    whereisit::engine::winrt::EngineClient client;
    client.StartAsync().get();
    auto ids = client.SearchIdsAsync(L"*").get();
    assert(ids.Size() >= 1);
    auto row = client.GetRowAsync(ids.GetAt(0)).get();
    assert(!row.name.empty());
    client.StopAsync().get();
    return 0;
}

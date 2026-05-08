#pragma once

#include <shared_mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>

namespace whereisit::engine::winrt {

struct RowData {
    uint32_t id{};
    std::wstring name;
    std::wstring parentPath;
};

class EngineClient {
public:
    winrt::Windows::Foundation::IAsyncAction StartAsync();
    winrt::Windows::Foundation::IAsyncAction SearchAsync(const winrt::hstring& query);
    winrt::Windows::Foundation::IAsyncOperation<winrt::Windows::Foundation::Collections::IVectorView<uint32_t>> SearchIdsAsync(const winrt::hstring& query);
    winrt::Windows::Foundation::IAsyncOperation<RowData> GetRowAsync(uint32_t id);
    winrt::Windows::Foundation::IAsyncAction StopAsync();

private:
    std::vector<uint32_t> SearchInternal(std::wstring_view query) const;

    mutable std::shared_mutex mutex_;
    bool started_{false};
    std::unordered_map<uint32_t, RowData> rows_;
};

} // namespace whereisit::engine::winrt

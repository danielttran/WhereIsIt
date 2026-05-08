#include "EngineClient.h"

#include <algorithm>
#include <stdexcept>

#include <winrt/base.h>

namespace whereisit::engine::winrt {

using winrt::Windows::Foundation::Collections::IVectorView;
using winrt::Windows::Foundation::Collections::single_threaded_vector;

winrt::Windows::Foundation::IAsyncAction EngineClient::StartAsync() {
    co_await winrt::resume_background();

    std::unique_lock lock(mutex_);
    if (started_) {
        co_return;
    }

    rows_.emplace(1, RowData{1, L"Readme.txt", L"C:\\"});
    rows_.emplace(2, RowData{2, L"WhereIsIt.log", L"C:\\Logs"});
    rows_.emplace(3, RowData{3, L"Data.bin", L"D:\\Data"});
    started_ = true;
}

std::vector<uint32_t> EngineClient::SearchInternal(std::wstring_view query) const {
    std::vector<uint32_t> ids;
    ids.reserve(rows_.size());

    const bool wildcard = (query == L"*");
    for (const auto& [id, row] : rows_) {
        if (wildcard || row.name.find(query) != std::wstring::npos) {
            ids.push_back(id);
        }
    }
    std::sort(ids.begin(), ids.end());
    return ids;
}

winrt::Windows::Foundation::IAsyncAction EngineClient::SearchAsync(const winrt::hstring& query) {
    co_await winrt::resume_background();
    std::shared_lock lock(mutex_);
    if (!started_) {
        throw winrt::hresult_illegal_method_call();
    }
    (void)SearchInternal(query.c_str());
}

winrt::Windows::Foundation::IAsyncOperation<IVectorView<uint32_t>> EngineClient::SearchIdsAsync(const winrt::hstring& query) {
    co_await winrt::resume_background();

    std::vector<uint32_t> ids;
    {
        std::shared_lock lock(mutex_);
        if (!started_) {
            throw winrt::hresult_illegal_method_call();
        }
        ids = SearchInternal(query.c_str());
    }

    auto result = single_threaded_vector<uint32_t>(std::move(ids));
    co_return result.GetView();
}

winrt::Windows::Foundation::IAsyncOperation<RowData> EngineClient::GetRowAsync(uint32_t id) {
    co_await winrt::resume_background();

    std::shared_lock lock(mutex_);
    if (!started_) {
        throw winrt::hresult_illegal_method_call();
    }

    const auto it = rows_.find(id);
    if (it == rows_.end()) {
        throw winrt::hresult_out_of_bounds();
    }
    co_return it->second;
}

winrt::Windows::Foundation::IAsyncAction EngineClient::StopAsync() {
    co_await winrt::resume_background();
    std::unique_lock lock(mutex_);
    started_ = false;
    rows_.clear();
}

} // namespace whereisit::engine::winrt

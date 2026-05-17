/*
 * WhereIsIt.Engine.Native.cpp
 *
 * Thin C-ABI wrapper around IndexingEngine (src/engine/native/cpp/Engine.h).
 * Exposes a flat handle-based API that the C# NativeEngineClient calls via P/Invoke.
 *
 * Include order is deliberate: Engine.h drags in windows.h via framework.h;
 * WhereIsIt.Engine.Native.h must come after so __declspec(dllexport) resolves first.
 */

/* Suppress min/max macros from windows.h so std::min/std::max compile cleanly */
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "Engine.h"        /* brings in windows.h, CoreTypes.h, RecordPool, StringPool, etc. */
#include "StringUtils.h"   /* WideToUtf8 / Utf8ToWide */

#define WHEREISIT_ENGINE_EXPORTS
#include "WhereIsIt.Engine.Native.h"

#include <algorithm>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

/* ------------------------------------------------------------------ */
/* Symbols defined in WhereIsIt.cpp (excluded from DLL) that Engine.cpp
   references. Provide them here.                                       */
/* ------------------------------------------------------------------ */

std::wstring FormatNumberWithCommas(size_t n) {
    std::wstring s = std::to_wstring(n);
    int pos = static_cast<int>(s.length()) - 3;
    while (pos > 0) { s.insert(pos, L","); pos -= 3; }
    return s;
}

/* ------------------------------------------------------------------ */
/* Internal state                                                       */
/* ------------------------------------------------------------------ */

struct EngineState {
    IndexingEngine engine;

    /*
     * prevResults tracks the last result set seen by engine_wait_results_changed
     * so WaitForNewResults can detect a change.  Reset to nullptr before each
     * new engine_search call so the very next result is always delivered.
     */
    std::shared_ptr<std::vector<uint32_t>> prevResults;
    mutable std::mutex mutex;
    bool stopped{ false };
};

static void copy_wide(wchar_t* dst, int dstMax, const std::wstring& src) noexcept {
    if (!dst || dstMax <= 0) return;
    const int n = static_cast<int>(
        std::min(static_cast<size_t>(dstMax - 1), src.size()));
    if (n > 0) src.copy(dst, static_cast<size_t>(n));
    dst[n] = L'\0';
}

/* ------------------------------------------------------------------ */
/* C API implementation                                                 */
/* ------------------------------------------------------------------ */

extern "C" {

EngineHandle engine_create() {
    try { return new EngineState(); }
    catch (...) { return nullptr; }
}

void engine_destroy(EngineHandle h) {
    if (!h) return;
    auto* s = static_cast<EngineState*>(h);

    // Ensure the engine is stopped (idempotent) so WasCleanStop() is decided
    // before we consider freeing. engine_stop() normally already did this.
    s->engine.Stop();

    if (s->engine.WasCleanStop()) {
        delete s;
    } else {
        // A worker (typically the initial, non-cancellable full-disk scan)
        // could not be joined within the shutdown budget and was detached.
        // It is still executing on this object's memory; deleting now would
        // be a use-after-free. The process is terminating anyway, so leak
        // the EngineState intentionally and let the OS reclaim it.
        OutputDebugStringW(
            L"[WhereIsIt] engine_destroy: worker still running; "
            L"leaking EngineState to avoid use-after-free.\n");
    }
}

void engine_start(EngineHandle h) {
    if (!h) return;
    static_cast<EngineState*>(h)->engine.Start();
}

void engine_stop(EngineHandle h) {
    if (!h) return;
    auto* s = static_cast<EngineState*>(h);
    {
        std::lock_guard<std::mutex> lk(s->mutex);
        s->stopped = true;
    }
    s->engine.Stop();
}

void engine_search(EngineHandle h, const wchar_t* query) {
    if (!h || !query) return;
    auto* s = static_cast<EngineState*>(h);
    {
        /* Capture current results so WaitForNewResults only fires when m_currentResults
         * actually changes — prevents stale results from a prior search being delivered
         * as "new" results for this search. */
        std::lock_guard<std::mutex> lk(s->mutex);
        s->prevResults = s->engine.GetSearchResults();
    }
    /* IndexingEngine::Search takes UTF-8 */
    s->engine.Search(WideToUtf8(std::wstring(query)));
}

void engine_sort(EngineHandle h, int sortKey, int descending) {
    if (!h) return;
    QuerySortKey key;
    switch (sortKey) {
        case 1:  key = QuerySortKey::Path; break;
        case 2:  key = QuerySortKey::Size; break;
        case 3:  key = QuerySortKey::Date; break;
        case 4:  key = QuerySortKey::Extension; break;
        case 5:  key = QuerySortKey::Attributes; break;
        default: key = QuerySortKey::Name; break;
    }
    static_cast<EngineState*>(h)->engine.Sort(key, descending != 0);
}

int engine_wait_results_changed(EngineHandle h, int timeoutMs, int* outCount) {
    if (!h || !outCount) return -1;
    auto* s = static_cast<EngineState*>(h);

    std::shared_ptr<std::vector<uint32_t>> prev;
    {
        std::lock_guard<std::mutex> lk(s->mutex);
        if (s->stopped) return -1;
        prev = s->prevResults;
    }

    auto next = s->engine.WaitForNewResults(prev, timeoutMs);

    {
        std::lock_guard<std::mutex> lk(s->mutex);
        if (s->stopped) return -1;
        if (!next || next == prev) {
            *outCount = 0;
            return 0; /* timeout — no change */
        }
        s->prevResults = next;
    }

    *outCount = static_cast<int>(next->size());
    return 1;
}

int engine_result_count(EngineHandle h) {
    if (!h) return 0;
    auto results = static_cast<EngineState*>(h)->engine.GetSearchResults();
    return results ? static_cast<int>(results->size()) : 0;
}

void engine_get_result_ids(EngineHandle h, uint32_t* buf, int count) {
    if (!h || !buf || count <= 0) return;
    auto results = static_cast<EngineState*>(h)->engine.GetSearchResults();
    if (!results) return;
    const int n = std::min(count, static_cast<int>(results->size()));
    std::copy(results->begin(), results->begin() + n, buf);
}

int engine_get_row(EngineHandle h, uint32_t recordId,
    wchar_t* name,       int nameMax,
    wchar_t* parentPath, int parentMax,
    uint64_t* sizeBytes,
    uint64_t* modifiedFileTime,
    uint16_t* attributes)
{
    if (!h) return -1;
    auto* s = static_cast<EngineState*>(h);

    if (recordId >= s->engine.GetRecordCount()) return -1;

    const auto row = s->engine.GetRowDisplayData(recordId);
    copy_wide(name, nameMax, row.Name);
    copy_wide(parentPath, parentMax, row.ParentPath);
    if (sizeBytes)        *sizeBytes        = row.FileSize;
    if (modifiedFileTime) *modifiedFileTime = row.FileTime;
    if (attributes)       *attributes       = row.Attributes;
    return 0;
}

int engine_get_status(EngineHandle h, wchar_t* buf, int bufMax) {
    if (!h || !buf || bufMax <= 0) return -1;
    copy_wide(buf, bufMax, static_cast<EngineState*>(h)->engine.GetCurrentStatus());
    return 0;
}

void engine_set_scope_roots(EngineHandle h,
    const wchar_t* const* paths, int count)
{
    if (!h) return;
    auto* s = static_cast<EngineState*>(h);
    IIndexEngine::IndexScopeConfig cfg;
    if (paths && count > 0) {
        for (int i = 0; i < count; ++i) {
            if (!paths[i]) continue;
            std::wstring p(paths[i]);
            // Ensure every include root ends with a backslash so the prefix
            // comparisons in IsRootEnabled / IsPathIncluded are unambiguous.
            if (!p.empty() && p.back() != L'\\' && p.back() != L'/') p += L'\\';
            cfg.IncludeRoots.emplace_back(std::move(p));
        }
    }
    s->engine.SetIndexScopeConfig(cfg);
}

} /* extern "C" */

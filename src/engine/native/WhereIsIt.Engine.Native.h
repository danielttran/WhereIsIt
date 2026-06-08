#pragma once

/* Pure C interface — no Windows headers required by consumers. */

#ifdef __cplusplus
extern "C" {
#endif

#ifdef WHEREISIT_ENGINE_EXPORTS
#  define ENGINE_API __declspec(dllexport)
#else
#  define ENGINE_API __declspec(dllimport)
#endif

#include <stdint.h>

typedef void* EngineHandle;

/* ------------------------------------------------------------------ */
/* Lifecycle                                                            */
/* ------------------------------------------------------------------ */

/* Native ABI version. Increment when adding a versioned API surface. */
ENGINE_API int engine_api_version(void);

/* Create engine state. Returns NULL on allocation failure. */
ENGINE_API EngineHandle engine_create(void);

/* Free all resources. engine_stop() must be called first. */
ENGINE_API void engine_destroy(EngineHandle h);

/* Start background indexing. Blocks briefly to register shared memory. */
ENGINE_API void engine_start(EngineHandle h);

/* Stop background indexing and wake any blocked engine_wait_results_changed call. */
ENGINE_API void engine_stop(EngineHandle h);

/* ------------------------------------------------------------------ */
/* Search and sort                                                      */
/* ------------------------------------------------------------------ */

/* Queue a search. Results arrive asynchronously via engine_wait_results_changed.
   query: UTF-16 string. Supports the full legacy query syntax including
   wildcards (*, ?), regex (regex:true …), boolean operators, and directives
   such as ext:, size:, date:. */
ENGINE_API void engine_search(EngineHandle h, const wchar_t* query);

/* Re-sort the current result set.
   sortKey: 0=name, 1=path, 2=size, 3=modified-date, 4=extension,
            5=attributes, 6=created-date, 7=accessed-date.
   descending: 0=ascending, 1=descending. */
ENGINE_API void engine_sort(EngineHandle h, int sortKey, int descending);

/* ------------------------------------------------------------------ */
/* Async result delivery                                                */
/* ------------------------------------------------------------------ */

/* Block until the result set changes or the timeout expires.
   outCount: filled with the new result count on change.
   Returns: 1=changed, 0=timeout (no change), -1=engine stopped/error.
   Call from a dedicated background thread. */
ENGINE_API int engine_wait_results_changed(EngineHandle h, int timeoutMs, int* outCount);

/* ------------------------------------------------------------------ */
/* Result accessors (call after wait or search)                         */
/* ------------------------------------------------------------------ */

/* Number of records in the current result set. */
ENGINE_API int engine_result_count(EngineHandle h);

/* Copy up to 'count' record IDs from the current result set into buf.
   IDs are opaque handles passed back to engine_get_row. */
ENGINE_API void engine_get_result_ids(EngineHandle h, uint32_t* buf, int count);

/* Race-free alternative to engine_result_count + engine_get_result_ids: copies
   up to 'capacity' IDs from the snapshot last observed by
   engine_wait_results_changed and (optionally via outTotal) reports that
   snapshot's full size, so the count and the copied IDs always agree.
   Returns the number of IDs written. */
ENGINE_API int engine_get_results(EngineHandle h, uint32_t* buf, int capacity, int* outTotal);

/* Fetch v1 display data for one record.
   Returns 0 on success, -1 if recordId is out of range.
   modifiedFileTime: Windows FILETIME ticks (100-ns intervals since 1601-01-01). */
ENGINE_API int engine_get_row(EngineHandle h, uint32_t recordId,
    wchar_t*  name,       int nameMax,
    wchar_t*  parentPath, int parentMax,
    uint64_t* sizeBytes,
    uint64_t* modifiedFileTime,
    uint16_t* attributes);

/* v2 adds optional Created/Accessed timestamps while retaining the v1 export
   for existing native consumers. */
ENGINE_API int engine_get_row_v2(EngineHandle h, uint32_t recordId,
    wchar_t*  name,       int nameMax,
    wchar_t*  parentPath, int parentMax,
    uint64_t* sizeBytes,
    uint64_t* modifiedFileTime,
    uint64_t* createdFileTime,
    uint64_t* accessedFileTime,
    uint16_t* attributes);

/* ------------------------------------------------------------------ */
/* Status                                                               */
/* ------------------------------------------------------------------ */

/* Current human-readable status string ("Indexing C:", "Ready", …).
   Returns 0 on success. buf receives a NUL-terminated wide string. */
ENGINE_API int engine_get_status(EngineHandle h, wchar_t* buf, int bufMax);

/* ------------------------------------------------------------------ */
/* Configuration (call before engine_start)                            */
/* ------------------------------------------------------------------ */

/* Restrict indexing to specific root paths.
   Pass paths=NULL or count=0 to scan all fixed drives (default).
   Setting a small temp directory makes the engine practical for testing. */
ENGINE_API void engine_set_scope_roots(EngineHandle h,
    const wchar_t* const* paths, int count);

#ifdef __cplusplus
} /* extern "C" */
#endif

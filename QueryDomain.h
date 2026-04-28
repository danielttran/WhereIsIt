#pragma once

#include <string>
#include "QueryEngine.h"

namespace querydomain {

// Phase 1 seam:
// Centralizes query-plan compilation behind a narrow API so the engine can
// switch to a refactored parser/matcher incrementally with parity tests.
QueryPlan CompilePlan(const std::string& rawQuery);

// Lightweight helper to keep Engine.cpp free of token-string heuristics.
bool HasInlineSortDirective(const std::string& rawQuery);

} // namespace querydomain

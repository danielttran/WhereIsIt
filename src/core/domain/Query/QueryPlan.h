#pragma once

#include "QueryParser.h"

namespace whereisit::core::query {

struct QueryPlan {
    bool hasInlineSort = false;
};

QueryPlan Compile(const QueryAst& ast);
bool HasInlineSortDirective(const QueryAst& ast);

}

#include "QueryPlan.h"

namespace whereisit::core::query {

bool HasInlineSortDirective(const QueryAst& ast) {
    if (ast.value.find("sort:") != std::string::npos) return true;
    for (const auto& c : ast.children) {
        if (HasInlineSortDirective(c)) return true;
    }
    return false;
}

QueryPlan Compile(const QueryAst& ast) {
    QueryPlan plan;
    plan.hasInlineSort = HasInlineSortDirective(ast);
    return plan;
}

}

#include "../../src/core/domain/Query/QueryParser.h"
#include "../../src/core/domain/Query/QueryPlan.h"

int main() {
    using namespace whereisit::core::query;
    auto ast = Parse("sort:size desc");
    auto plan = Compile(ast);
    return plan.hasInlineSort ? 0 : 1;
}

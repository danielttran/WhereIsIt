#include "QueryDomain.h"

namespace querydomain {

QueryPlan CompilePlan(const std::string& rawQuery)
{
    return BuildQueryPlan(rawQuery);
}

bool HasInlineSortDirective(const std::string& rawQuery)
{
    return rawQuery.find("sort:") != std::string::npos ||
           rawQuery.find("desc") != std::string::npos ||
           rawQuery.find("asc") != std::string::npos;
}

} // namespace querydomain

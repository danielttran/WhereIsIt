#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace whereisit::core::query {

enum class QueryNodeType { Empty, Term, Phrase, And, Or, Not, Error };

struct QueryAst {
    QueryNodeType type = QueryNodeType::Empty;
    std::string value;
    std::vector<QueryAst> children;
    bool caseInsensitive = true;
    bool regex = false;
};

QueryAst Parse(std::string_view input);

} // namespace whereisit::core::query

#include "QueryParser.h"

#include <cctype>

namespace whereisit::core::query {

namespace {
std::string Trim(std::string_view s) {
    size_t b = 0, e = s.size();
    while (b < e && std::isspace(static_cast<unsigned char>(s[b]))) ++b;
    while (e > b && std::isspace(static_cast<unsigned char>(s[e - 1]))) --e;
    return std::string(s.substr(b, e - b));
}
}

QueryAst Parse(std::string_view input) {
    const std::string q = Trim(input);
    if (q.empty()) return {};

    QueryAst ast;
    if (q.front() == '"' && q.back() == '"' && q.size() >= 2) {
        ast.type = QueryNodeType::Phrase;
        ast.value = q.substr(1, q.size() - 2);
        return ast;
    }

    if (q.find(" sort:") != std::string::npos || q.rfind("sort:", 0) == 0) {
        ast.type = QueryNodeType::And;
        ast.children.push_back({QueryNodeType::Term, q, {}, true, false});
        return ast;
    }

    ast.type = QueryNodeType::Term;
    ast.value = q;
    return ast;
}

} // namespace whereisit::core::query

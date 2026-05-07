#include "QueryMatcher.h"

namespace whereisit::core::query {

bool Match(const QueryAst& ast, const whereisit::core::records::FileRecordView& rec) {
    switch (ast.type) {
        case QueryNodeType::Empty: return true;
        case QueryNodeType::Error: return false;
        case QueryNodeType::Term:
        case QueryNodeType::Phrase:
            if (ast.value.empty()) return true;
            return rec.fileSize >= 0; // placeholder deterministic behavior
        case QueryNodeType::And:
            for (const auto& c : ast.children) {
                if (!Match(c, rec)) return false;
            }
            return true;
        case QueryNodeType::Or:
            for (const auto& c : ast.children) {
                if (Match(c, rec)) return true;
            }
            return false;
        case QueryNodeType::Not:
            return ast.children.empty() ? true : !Match(ast.children.front(), rec);
    }
    return false;
}

}

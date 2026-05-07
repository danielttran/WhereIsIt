#pragma once

#include "QueryParser.h"
#include "../Records/FileRecordView.h"

namespace whereisit::core::query {

bool Match(const QueryAst& ast, const whereisit::core::records::FileRecordView& rec);

}

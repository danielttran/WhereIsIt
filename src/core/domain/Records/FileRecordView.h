#pragma once

#include <cstdint>

#include "../../../engine/native/cpp/CoreTypes.h"

namespace whereisit::core::records {

struct FileRecordView {
    uint32_t mftIndex = 0;
    uint32_t parentMftIndex = 0;
    uint32_t parentRecordIndex = 0;
    uint32_t lastModifiedEpoch = 0;
    uint32_t fileSize = 0;
    uint16_t fileAttributes = 0;
    bool isGiantFile = false;

    static FileRecordView FromLegacy(const FileRecord& rec) noexcept {
        FileRecordView view;
        view.mftIndex = rec.MftIndex;
        view.parentMftIndex = rec.ParentMftIndex;
        view.parentRecordIndex = rec.ParentRecordIndex;
        view.lastModifiedEpoch = rec.LastModifiedEpoch;
        view.fileSize = rec.FileSize;
        view.fileAttributes = static_cast<uint16_t>(rec.FileAttributes);
        view.isGiantFile = rec.IsGiantFile != 0;
        return view;
    }
};

} // namespace whereisit::core::records

#pragma once
#include <stdint.h>

enum class DriveFileSystem {
    Unknown,
    NTFS,
    Generic
};

enum class QuerySortKey { Name, Path, Size, Date };

#pragma pack(push, 1)
struct FileRecord {
    uint32_t NamePoolOffset;
    uint32_t ParentMftIndex;
    uint32_t MftIndex;
    uint32_t LastModifiedEpoch;
    uint32_t FileSize;
    uint16_t MftSequence;
    uint16_t ParentSequence;
    uint32_t DriveIndex       : 6;
    uint32_t IsGiantFile      : 1;
    uint32_t FileAttributes   : 16;
    uint32_t DirSizeComputed  : 1;   // set after directory sizes have been propagated
    uint32_t Reserved         : 8;
    uint32_t ParentRecordIndex;
};
#pragma pack(pop)
static_assert(sizeof(FileRecord) == 32, "FileRecord MUST be exactly 32 bytes for cache alignment");

// Internal NTFS Direct-Disk Structures
#pragma pack(push, 1)
struct MFT_RECORD_HEADER {
    uint32_t Magic; uint16_t UpdateSeqOffset; uint16_t UpdateSeqSize; uint64_t LSN;
    uint16_t SequenceNumber; uint16_t HardLinkCount; uint16_t AttributeOffset;
    uint16_t Flags; uint32_t UsedSize; uint32_t AllocatedSize; uint64_t BaseRecord; uint16_t NextAttributeID;
};
struct MFT_ATTRIBUTE {
    uint32_t Type; uint32_t Length; uint8_t NonResident; uint8_t NameLength;
    uint16_t NameOffset; uint16_t Flags; uint16_t AttributeID;
};
struct MFT_RESIDENT_ATTRIBUTE { MFT_ATTRIBUTE Header; uint32_t ValueLength; uint16_t ValueOffset; uint8_t Flags; uint8_t Reserved; };
struct MFT_FILE_NAME {
    uint64_t ParentDirectory; uint64_t CreationTime; uint64_t ChangeTime; uint64_t LastWriteTime;
    uint64_t LastAccessTime; uint64_t AllocatedSize; uint64_t DataSize; uint32_t FileAttributes;
    uint32_t AlignmentOrReserved; uint8_t NameLength; uint8_t NameNamespace; wchar_t Name[1];
};
#pragma pack(pop)

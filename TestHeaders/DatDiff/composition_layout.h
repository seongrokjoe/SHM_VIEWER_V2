#pragma once

#define DAT_PACK_ONE __pragma(pack(push, 1))
DAT_PACK_ONE
struct PackedPart { char tag; int value; };
#pragma pack(pop)

#if 0
#pragma pack(16)
#endif

#pragma pack(push, 2)
struct CompositionLayout {
    char prefix;
    unsigned int low : 3;
    unsigned int : 5;
    unsigned int high : 8;
    unsigned int : 0;
    short tail;
    PackedPart items[2][2];
};
#pragma pack(pop)

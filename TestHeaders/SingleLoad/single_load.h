#pragma once

enum class SampleMode : unsigned short { Idle = 0, Ready = 1, Running = 2 };

struct SampleItem {
    short nSpare;
    int quantity;
    double price;
    char label[20];
};

#pragma pack(push, 1)
struct PackedSettings {
    char enabled;
    long timeout;
};
#pragma pack(pop)

struct SingleLoadSample {
    int revision;
    char title[20];
    SampleMode mode;
    SampleItem items[3];
    int matrix[2][3];
    PackedSettings settings;
    long tail;
};

// Intentionally not referenced by SingleLoadSample: add via the type dialog.
namespace Extra {
    struct Info { long code; char note[20]; };
    typedef unsigned long Counter;
}

struct LargeItem { int value; short spare; };
struct SingleLoad20000 { LargeItem items[20000]; long tail; };
struct SingleLoad100000 { LargeItem items[100000]; long tail; };

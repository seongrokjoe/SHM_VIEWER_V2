#pragma once
#pragma pack(push, 1)
struct Settings {
    short mode;
    int level;
    int added;
};
struct Root {
    int common;
    Settings settings;
    int samples[3];
};
#pragma pack(pop)

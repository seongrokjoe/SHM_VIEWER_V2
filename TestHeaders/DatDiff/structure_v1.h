#pragma once
#pragma pack(push, 4)
struct Settings {
    int level;
    short mode;
};
struct Root {
    int common;
    Settings settings;
    int samples[2];
};
#pragma pack(pop)

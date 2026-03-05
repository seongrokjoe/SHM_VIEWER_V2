// Test header for SHM_VIEWER
// Covers: enum, struct, union, bitfield, typedef, nested struct, array, spare fields

#pragma once

// ─── Defines ───
#define MAX_NODES       10
#define NAME_LEN        32
#define MAX_SENSORS     4

// ─── Enums ───
enum eStatus {
    eIdle,          // 0
    eRun = 5,
    eStop,          // 6
    eError = 100,
};

typedef enum _eMode {
    eModeNone = 0,
    eModeAuto = 1,
    eModeManual = 2,
} eMode;

// ─── Bitfield union ───
typedef union _uFlags {
    unsigned int all;
    struct {
        unsigned int enable : 1;
        unsigned int ready  : 1;
        unsigned int error  : 1;
        unsigned int spare  : 29;
    } bits;
} uFlags;

// ─── Simple struct ───
typedef struct tagSensorData {
    float    value;
    int      count;
    uFlags   flags;
    char     spare[8];
} SensorData;

// ─── Nested struct with arrays ───
typedef struct tagDynCfg {
    int             sizetagDynCfg;
    eStatus         status;
    eMode           mode;
    char            name[NAME_LEN];
    SensorData      sensors[MAX_SENSORS];
    double          ratio;
    unsigned int    nodeCount;
    int             nodeIds[MAX_NODES];
    uFlags          systemFlags;
    BYTE            reserved[16];
} tagDynCfg;

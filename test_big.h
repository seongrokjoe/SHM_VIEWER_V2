// test_big.h
// 40MB+ 구조체 로드 테스트용 헤더 파일
// 루트 구조체: tagBigSHM (총 크기 약 41.3MB)
// - Lazy Loading 동작 확인: channels[52] 배열 원소 52개
// - sensors[500] 배열 → 원소 500개 Lazy Loading 확인

#pragma once

// ==================== #define ====================
#define MAX_SENSORS      500
#define MAX_LOG_ENTRIES  200
#define CALIB_POINTS      50
#define MAX_CHANNELS      52
#define SYS_NAME_LEN     128
#define SENSOR_NAME_LEN   64
#define CH_NAME_LEN       64
#define LOG_MSG_LEN      256
#define ERR_MSG_LEN      128
#define NOTE_LEN          64

// ==================== enum ====================
enum eSensorType {
    eSensorUnknown  = 0,
    eSensorTemp     = 1,
    eSensorPressure = 2,
    eSensorFlow     = 3,
    eSensorVibration= 4,
    eSensorCurrent  = 5,
    eSensorVoltage  = 6,
    eSensorMax      = 7,
};

enum eSensorStatus {
    eSensorIdle     = 0,
    eSensorRunning  = 1,
    eSensorWarning  = 5,
    eSensorError    = 10,
    eSensorOffline  = 99,
};

enum eLogLevel {
    eLogDebug   = 0,
    eLogInfo    = 1,
    eLogWarning = 2,
    eLogError   = 3,
    eLogFatal   = 4,
};

enum eChannelMode {
    eChModeOff      = 0,
    eChModeMonitor  = 1,
    eChModeControl  = 2,
    eChModeCalib    = 3,
};

// ==================== typedef ====================
typedef unsigned char  BYTE;
typedef unsigned short WORD;
typedef unsigned int   UINT;
typedef unsigned long  DWORD;

// ==================== union ====================
typedef union tagDataVal {
    int    iVal;
    float  fVal;
    BYTE   bytes[4];
} DataVal;

// ==================== bitfield struct ====================
typedef struct tagFlags {
    unsigned int enabled    : 1;
    unsigned int alarm      : 1;
    unsigned int fault      : 1;
    unsigned int calibrated : 1;
    unsigned int spare      : 28;
} Flags;

// ==================== simple structs ====================
typedef struct tagPoint3D {
    double x;
    double y;
    double z;
} Point3D;

typedef struct tagCalibration {
    Point3D  points[CALIB_POINTS];
    float    coefficients[10];
    char     note[NOTE_LEN];
    int      pointCount;
    BYTE     reserved[4];
} Calibration;

// ==================== Sensor (~1552 bytes) ====================
typedef struct tagSensor {
    int           id;
    eSensorType   type;
    char          name[SENSOR_NAME_LEN];
    float         rawValue;
    float         filteredValue;
    double        timestamp;
    DataVal       extra;
    Flags         flags;
    Calibration   calib;
    eSensorStatus status;
    int           errorCount;
    char          lastError[ERR_MSG_LEN];
} Sensor;

// ==================== LogEntry (~280 bytes aligned) ====================
typedef struct tagLogEntry {
    int       id;
    eLogLevel level;
    double    timestamp;
    char      message[LOG_MSG_LEN];
    int       sourceId;
    BYTE      spare[4];
} LogEntry;

// ==================== Channel (~832KB) ====================
typedef struct tagChannel {
    int          channelId;
    eChannelMode mode;
    char         name[CH_NAME_LEN];
    Sensor       sensors[MAX_SENSORS];
    int          activeSensors;
    double       sampleRate;
    UINT         channelFlags;
    LogEntry     log[MAX_LOG_ENTRIES];
    int          logCount;
    BYTE         reserved[4];
} Channel;

// ==================== Root: tagBigSHM (~41.3MB) ====================
// Channel 1개 ≈ 832,088 bytes
// Channel × 52 ≈ 43,268,576 bytes ≈ 41.3MB
typedef struct tagBigSHM {
    int      version;
    char     systemName[SYS_NAME_LEN];
    Channel  channels[MAX_CHANNELS];
    UINT     totalErrors;
    double   startTime;
    double   lastUpdateTime;
    BYTE     spare[8];
} tagBigSHM;

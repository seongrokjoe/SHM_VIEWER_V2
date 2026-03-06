# SHM_VIEWER 프로젝트 지침 (Claude Code용)

## 프로젝트 개요

- **앱 이름**: SHM_VIEWER
- **목적**: Windows 환경에서 여러 개의 Named Shared Memory를 실시간으로 모니터링하는 데스크탑 앱
- **개발 환경**: Visual Studio 2022, C# (.NET 8), WPF
- **핵심 의존성**: ClangSharp (Microsoft 공식 C# 래퍼, NuGet), libclang (LLVM Apache 2.0)

---

## 배경 및 기술 컨텍스트

### 모니터링 대상 앱 (기존 앱) 정보

- Visual Studio 2017로 개발된 C++ 앱
- Windows Named File Mapping 방식으로 Shared Memory 구현
- `esLib::esSharedMemory` 라는 외부 라이브러리 사용
- 내부적으로 WinAPI `CreateFileMapping` / `OpenFileMapping` / `MapViewOfFile` 사용
- 여러 개의 Shared Memory가 동시에 운용됨
- 각 Shared Memory마다 고유한 이름(문자열)과 메인 구조체가 대응됨
- 구조체 정의는 여러 개의 C++ 헤더 파일(.h)에 분산되어 있음
- 메인 구조체 내부에 다른 구조체들이 중첩되어 매우 복잡하게 얽혀 있음

### 기존 앱의 SHM 사용 예시

```cpp
cppm_psmDynCfg = new esLib::esSharedMemory();
if (m_psmDynCfg->Open("DYNAMIC_CONFIGURE_MEMORY", sizeof(tagDynCfg)) == true)
{
    m_pDyn = (tagDynCfg*)m_psmDynCfg->addr;
    m_pDyn->sizetagDynCfg = sizeof(tagDynCfg);
}
```

### SHM 접근 방식 (WinAPI)

```cpp
// 내부적으로 이렇게 동작함
HANDLE h = OpenFileMapping(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, "SHM_NAME");
void* addr = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, 0);
```

---

## 요구사항 (전체)

### 1. 헤더 파일 업로드

- 구조체 정보가 담긴 C++ 헤더 파일(.h)을 여러 개 업로드할 수 있어야 함
- 드래그 앤 드롭 및 파일 선택 버튼 지원
- 업로드된 파일 목록 표시 및 개별 제거 가능
- 업로드된 모든 헤더 파일은 하나의 파일 풀(pool)로 통합하여 탐색에 사용

### 2. SHM 등록 및 Load

- SHM 이름(문자열)과 메인 struct 이름을 입력하고 Load 버튼 클릭
- 여러 개의 SHM을 동시에 Load할 수 있어야 함 (탭 방식으로 각각 표시)
- Load / Unload 기능 모두 지원
- 갱신 주기 설정 가능 (500ms, 1s, 5s, 수동)

### 3. 헤더 파싱 → 트리 구조 매핑

- Load 시 업로드된 헤더 파일 풀에서 메인 struct를 탐색
- 메인 struct를 트리 최상단에, 하위에 멤버들을 계층적으로 표시
- Visual Studio 디버거 Watch 창과 동일한 형태의 트리뷰
- 표시 컬럼: NAME, TYPE, OFFSET, SIZE, VALUE

### 4. 중첩 구조체 재귀 탐색

- 멤버 중 사용자 정의 타입(예: `ABC m_abc;`)이 있으면
  업로드된 파일 풀에서 해당 타입 정의를 찾아 재귀적으로 탐색
- 탐색 깊이 제한 없음 (완전한 재귀)

### 5. 타입별 값 표시

- 각 멤버의 타입에 맞게 값을 해석하여 표시
- 타입별 처리 방식은 아래 "타입 지원 명세" 참고

### 6. 우클릭 → 2진수/상세 표시

- 트리의 임의 항목에서 우클릭 시 컨텍스트 메뉴 표시
- 팝업에서 해당 값을 DEC / HEX / BIN / OCT 형태로 모두 표시
- enum 타입인 경우 enum 멤버 목록과 현재값 하이라이트 표시
- Offset, Size, Type 정보도 함께 표시

### 7. Load 실패 처리

- 헤더 파싱 중 타입을 찾지 못하는 경우 즉시 실패하지 않고
  미발견 타입 전체 목록을 수집한 뒤 Load 실패 처리
- 실패 시 팝업으로 미발견 타입 목록 전체 표시
  (예: `tagDynCfg.m_sensor → SensorData 미발견`)
- 해당 타입이 정의된 헤더 파일을 추가 업로드하도록 안내

### 8. 개발 환경

- Visual Studio 2022, C# (.NET 8), WPF
- 기존 앱(VS2017 C++)과 개발 환경이 달라도 무관

---

## 지원해야 할 C++ 문법 명세 (파서)

### `#define`

```cpp
#define MAX_SIZE       10
#define BUFFER_LEN     256
#define MAX_NODES      (MAX_SIZE * 2)    // 표현식
#define BYTE           unsigned char     // 타입 별칭처럼 사용
```

- Define 테이블을 먼저 전체 수집
- 이후 파싱 시 심볼 치환 (배열 크기 등에 사용)
- 기본 사칙연산 표현식 평가 지원

### `typedef`

```cpp
typedef unsigned int   UINT;
typedef unsigned char  BYTE;
typedef unsigned short WORD;
typedef unsigned long  DWORD;
typedef struct tagABC { ... } ABC;
typedef struct tagABC ABC;         // 선언과 분리된 경우
typedef void (*FuncPtr)(int);      // 함수 포인터 → 크기=8(포인터)
typedef char NameStr[64];          // 배열 typedef
```

### `const`

```cpp
const int MAX = 10;   // Define 테이블에 통합하여 처리
```

### `enum` (값 개별 지정 포함)

```cpp
// 모든 값 지정
enum eFlowmeter {
    eFlowOne  = 5,
    eFlowTwo  = 10,
    eFlowMax  = 20,
};

// 혼합 (일부만 값 지정, 나머지는 이전+1)
enum eStatus {
    eIdle,          // 자동 = 0
    eRun = 5,
    eStop,          // 자동 = 6
    eError = 100,
};

typedef enum _eMode { ... } eMode;
```

- 각 항목의 값을 EnumTable에 저장
- 화면에서 숫자 대신 enum 이름 표시 (예: `eRun(5)`)

### `struct`

```cpp
// 기본
struct ABC { int x; double y; };

// typedef struct
typedef struct tagDynCfg { ... } tagDynCfg;

// 중첩 멤버
struct Parent {
    ABC    m_abc;
    int    count;
};

// 배열 멤버
struct Config {
    char   name[64];
    int    data[MAX_SIZE];   // #define 치환
    ABC    nodes[10];        // struct 배열
};

// 익명 중첩 struct
struct Outer {
    struct {
        int x;
        int y;
    } point;
};

// 전방 선언 (다른 파일에 정의)
struct ABC;
```

### `union`

```cpp
// 기본
union DataVal {
    int    iVal;
    float  fVal;
    char   cVal[4];
};

// struct 내부 union
struct Packet {
    int type;
    union {
        int   intData;
        float floatData;
    } data;
};

// 비트필드 포함 union
typedef union _uStatus {
    unsigned int all;
    struct {
        unsigned int bit0 : 1;
        unsigned int bit1 : 1;
        unsigned int spare : 30;
    } bits;
} uStatus;
```

- union: 모든 멤버 offset=0, struct 크기 = 가장 큰 멤버 크기

### spare / reserved 필드

```cpp
char  spare[10];
BYTE  reserved[4];
int   dummy;
```

- 일반 배열과 동일하게 처리
- 이름에 `spare` / `reserved` / `dummy` 포함 시 UI에서 회색으로 표시

### 비트필드

```cpp
struct Flags {
    unsigned int enable : 1;
    unsigned int mode   : 3;
    unsigned int spare  : 28;
};
```

### `#pragma pack`

```cpp
#pragma pack(push, 1)
struct PackedData { ... };
#pragma pack(pop)
```

- push/pop 스택으로 현재 pack 값 추적
- Offset 계산 시 반영

---

## 타입 지원 명세

### 기본형 크기표 (x64 Windows 기준)

| 타입 | 크기 | 비고 |
|------|------|------|
| `char` | 1 | |
| `unsigned char` / `BYTE` | 1 | |
| `short` | 2 | |
| `unsigned short` / `WORD` | 2 | |
| `int` | 4 | |
| `unsigned int` / `UINT` | 4 | |
| `long` | 4 | **Windows에서 long = 4바이트** (Linux와 다름) |
| `unsigned long` / `DWORD` | 4 | |
| `long long` | 8 | |
| `unsigned long long` | 8 | |
| `float` | 4 | |
| `double` | 8 | |
| `bool` | 1 | |
| `wchar_t` | 2 | |
| pointer (`*`) | 8 | x64 기준 |

### 타입별 값 표시 방식

| 타입 | 표시 방식 |
|------|----------|
| `char` | int8 값 또는 ASCII 문자 |
| `char[N]` | 문자열 (null 종단 감지), null 없으면 HEX 덤프 |
| `short` / `int` / `long` | 부호 있는 정수 |
| unsigned 계열 | 부호 없는 정수 |
| `long long` | 64비트 정수 |
| `float` | 소수점 표시 |
| `double` | 소수점 표시 |
| `bool` | `true` / `false` |
| pointer | 주소값 (`0x...`) |
| enum | 숫자 + EnumTable에서 이름 검색 → `eRun(5)` |
| 비트필드 | 비트 마스크 적용 후 값 추출 |
| union | 각 멤버를 동일 offset에서 각각 해석하여 모두 표시 |
| spare / `char[]` | HEX 덤프 표시 (회색) |

---

## 전체 시스템 아키텍처

```
┌─────────────────────────────────────────────────────────────────┐
│                         SHM_VIEWER                              │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                    HeaderParser Engine                   │   │
│  │  (ClangSharp 기반 - 컴파일러 수준 파싱)                   │   │
│  │                                                          │   │
│  │  Phase1: PreProcessor → Phase2: ClangSharp AST 파싱     │   │
│  │  → Phase3: TypeDatabase 구축 → Phase4: TypeResolver     │   │
│  └──────────────────────────┬──────────────────────────────┘   │
│                             │                                   │
│  ┌──────────────────────────▼──────────────────────────────┐   │
│  │                     TypeDatabase                         │   │
│  │  StructTable │ EnumTable │ TypedefTable │ DefineTable    │   │
│  └──────────────────────────┬──────────────────────────────┘   │
│                             │                                   │
│  ┌──────────────────────────▼──────────────────────────────┐   │
│  │           TypeResolver + OffsetCalculator                │   │
│  │  재귀 탐색 │ alignment 계산 │ pragma pack 반영           │   │
│  │  UnresolvedTypeList 수집                                 │   │
│  └──────────────────────────┬──────────────────────────────┘   │
│                             │                                   │
│  ┌────────────────┐  ┌──────▼───────────┐  ┌───────────────┐  │
│  │   ShmReader    │  │   DataMapper     │  │   WPF UI      │  │
│  │  (WinAPI)      │→ │                  │→ │               │  │
│  │ OpenFileMapping│  │ byte[] + TypeInfo│  │ TreeView      │  │
│  │ MapViewOfFile  │  │ → 값 해석        │  │ BinaryViewer  │  │
│  │ 주기적 Refresh │  │ enum명 매핑      │  │ Multi-Tab SHM │  │
│  └────────────────┘  └──────────────────┘  └───────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 모듈별 구현 명세

### Module 1: HeaderParser (ClangSharp 기반)

```csharp
// ClangSharp으로 AST를 순회하여 TypeDatabase 구축
var index = CXIndex.Create();
var tu = CXTranslationUnit.Parse(index, "combined.h", ...);

tu.Cursor.VisitChildren((cursor, parent) => {
    switch (cursor.Kind) {
        case CXCursorKind.CXCursor_StructDecl:   // struct 처리
        case CXCursorKind.CXCursor_UnionDecl:    // union 처리
        case CXCursorKind.CXCursor_EnumDecl:     // enum 처리
        case CXCursorKind.CXCursor_TypedefDecl:  // typedef 처리
        case CXCursorKind.CXCursor_FieldDecl:    // 멤버 처리
            // name, type, size, offset 자동 수집
            // cursor.Type.SizeOf    → 크기 (bytes)
            // cursor.OffsetOfField  → 오프셋 (bits → /8)
    }
    return CXChildVisitResult.CXChildVisit_Recurse;
});
```

- 업로드된 모든 헤더 파일을 하나의 가상 헤더에 통합 후 파싱
- `#define`, `#pragma pack`, `typedef` 체인 자동 처리
- 2-pass 파싱: 1st pass 이름 수집 → 2nd pass 본문 파싱 (전방 선언 대응)

### Module 2: TypeDatabase 자료구조

```csharp
class TypeInfo {
    string           Name;          // 타입 이름
    int              TotalSize;     // 전체 크기 (bytes)
    bool             IsUnion;       // union 여부
    List<MemberInfo> Members;       // 멤버 목록
}

class MemberInfo {
    string   Name;           // 멤버 이름
    string   TypeName;       // 원본 타입 문자열
    TypeInfo ResolvedType;   // 재귀 참조
    int      Offset;         // 구조체 내 바이트 오프셋
    int      Size;           // 바이트 크기
    int      ArrayCount;     // 1=단일, N=배열[N]
    bool     IsPointer;      // 포인터 여부
    int      BitFieldWidth;  // 0=비트필드 아님, N=비트 수
    bool     IsSpare;        // spare/reserved/dummy 감지
}

class EnumInfo {
    string                     Name;
    Dictionary<string, long>   Members; // {"eRun": 5, "eStop": 6}
}
```

### Module 3: ShmReader (WinAPI P/Invoke)

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr OpenFileMapping(uint dwDesiredAccess,
    bool bInheritHandle, string lpName);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject,
    uint dwDesiredAccess, uint dwFileOffsetHigh,
    uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(IntPtr hObject);

// FILE_MAP_READ = 0x0004
// 스냅샷 방식으로 읽기 (race condition 최소화)
public byte[] ReadSnapshot(string shmName, int size) {
    var handle = OpenFileMapping(FILE_MAP_READ, false, shmName);
    var addr   = MapViewOfFile(handle, FILE_MAP_READ, 0, 0, (UIntPtr)size);
    var buffer = new byte[size];
    Marshal.Copy(addr, buffer, 0, size);
    UnmapViewOfFile(addr);
    return buffer;
}
```

### Module 4: DataMapper

- `byte[]` (SHM 원시 데이터) + `TypeInfo` → `TreeNodeViewModel`
- Offset 기반으로 각 멤버의 bytes를 추출하여 타입에 맞게 변환
- enum 타입은 숫자로 읽은 후 EnumTable에서 이름 검색
- union은 동일 offset에서 모든 멤버를 각각 해석

### Module 5: WPF UI (MVVM 패턴)

**메인 화면 레이아웃:**

```
┌──────────────────────────────────────────────────────────────┐
│  [헤더 파일 업로드 (드래그 앤 드롭 또는 버튼)]               │
│  config.h  define.h  struct_a.h  [+추가] [전체삭제]          │
├──────────────────────────────────────────────────────────────┤
│  [SHM_A 탭] [SHM_B 탭] [SHM_C 탭]              [+ 탭 추가]  │
├──────────────────────────────────────────────────────────────┤
│  SHM Name : [DYNAMIC_CONFIGURE_MEMORY          ]             │
│  Struct   : [tagDynCfg                         ]             │
│  Refresh  : [500ms  1s  5s  수동]     [Load] [Unload]        │
├──────────────────────────────────────────────────────────────┤
│  상태: Loaded | Total Size: 2048 bytes | 마지막 갱신: ...     │
├──────────────────────────────────────────────────────────────┤
│  NAME                  TYPE          OFFSET  SIZE   VALUE    │
│  ▼ tagDynCfg                                 2048           │
│    │ sizetagDynCfg      long          +0      4      2048   │
│    │ status             eStatus       +4      4      eRun(1)│
│    ▼ m_abc              ABC           +8      48            │
│    │   │ x              int           +8      4      100    │
│    │   │ value          double        +12     8      3.14   │
│    │   └ name           char[32]      +20     32     "hello"│
│    ▼ u_data             union DataVal +56     4             │
│    │   │ iVal           int           +56     4      ...    │
│    │   └ fVal           float         +56     4      ...    │
│    │ spare              char[10]      +60     10  (회색표시)│
└──────────────────────────────────────────────────────────────┘
```

**우클릭 팝업 (상세/2진수 표시):**

```
┌──────────────────────────────────────────┐
│  status  =  eRun  (1)                    │
├──────────────────────────────────────────┤
│  DEC : 1                                 │
│  HEX : 0x00000001                        │
│  BIN : 0000 0000  0000 0000              │
│        0000 0000  0000 0001              │
│  OCT : 0o00000001                        │
├──────────────────────────────────────────┤
│  Offset: +4  |  Size: 4 bytes            │
│  Type  : enum eStatus                   │
├──────────────────────────────────────────┤
│  Enum 값 목록:                           │
│    eIdle  = 0                            │
│  > eRun   = 1   <- 현재값               │
│    eStop  = 6                            │
│    eError = 100                          │
└──────────────────────────────────────────┘
```

**Load 실패 팝업:**

```
┌─────────────────────────────────────────────┐
│  Load 실패 - 미발견 타입 목록               │
├─────────────────────────────────────────────┤
│  tagDynCfg.m_sensor  →  SensorData  미발견  │
│  tagDynCfg.m_ctrl    →  CtrlInfo    미발견  │
│  ABC.eMode           →  eOpMode     미발견  │
├─────────────────────────────────────────────┤
│  해당 타입이 정의된 헤더 파일을              │
│  추가로 업로드 후 다시 Load하세요.           │
│                               [확인]        │
└─────────────────────────────────────────────┘
```

---

## 기술 스택

| 항목 | 선택 |
|------|------|
| 언어 | C# (.NET 8) |
| UI 프레임워크 | WPF (MVVM 패턴) |
| IDE | Visual Studio 2022 |
| C++ 헤더 파서 | ClangSharp (NuGet: `ClangSharp`) |
| LLVM 바이너리 | `libclang.dll` (LLVM 공식 릴리즈, Apache 2.0) |
| SHM 접근 | P/Invoke → WinAPI (`kernel32.dll`) |

### ClangSharp 입수 방법

- NuGet: `Install-Package ClangSharp`
- 라이선스: MIT (상업적 사용 무료, 사내 사용 가능)

### libclang.dll

- LLVM 공식 릴리즈에서 `LLVM-xx.x.x-win64.exe` 추출
- 라이선스: Apache 2.0 (사내 사용 가능)

---

## 개발 단계 (Phase)

### Phase 1 (1~2주): HeaderParser 구현 및 단위 테스트
- ClangSharp 연동 및 AST 순회
- TypeDatabase 구축 (struct/union/enum/typedef/define)
- UnresolvedTypeList 수집 로직
- 가상 헤더 파일로 단위 테스트

### Phase 2 (3~4일): TypeResolver + OffsetCalculator
- 재귀 탐색 로직
- 기본형 크기 테이블 (Windows x64 기준)
- `#pragma pack` 반영
- `sizeof` 검증 로직

### Phase 3 (3~4일): ShmReader + DataMapper
- WinAPI P/Invoke 연동
- 타입별 `byte[]` → 값 변환 로직
- enum 이름 매핑
- 비트필드 값 추출

### Phase 4 (1주): WPF UI 구현
- 헤더 파일 업로드 패널 (드래그앤드롭)
- TreeView (MVVM, HierarchicalDataTemplate)
- 다중 SHM 탭
- 우클릭 컨텍스트 메뉴 + 팝업
- Load 실패 에러 다이얼로그
- spare 필드 회색 표시

### Phase 5 (3~4일): 통합 테스트 + 예외처리
- 실제 헤더 파일로 파싱 검증
- SHM 연결 실패 시 에러 처리
- 자동 갱신 타이머 안정화

---

## 리스크 및 대응 방안

| 리스크 | 대응 방안 |
|--------|----------|
| `long` 크기 혼동 | Windows = 4바이트 명시, 설정에서 오버라이드 가능 |
| pragma pack 없이 offset 불일치 | struct 예상 크기 vs SHM 실제 크기 비교 검증란 제공 |
| 매크로 표현식 (`A*2+1`) | 기본 사칙연산 expr 평가기 구현 (ClangSharp이 대부분 처리) |
| 전방 선언으로 정의 못 찾는 경우 | 2-pass 파싱 (1st: 이름수집, 2nd: 본문파싱) |
| 함수 포인터 typedef | 크기=포인터크기(8)로 처리, 값=주소 표시 |
| SHM 쓰기 중 읽기 (Race Condition) | 스냅샷 방식으로 읽기, 시각적으로 갱신 표시 |
| 복잡한 `#define` 체인 | Define 테이블 먼저 전체 수집 후 치환 |
| 다중 헤더 파일 중복 정의 | 나중에 로드된 파일 우선 (경고 표시) |

---

## 지원하는 C++ 문법 체크리스트

- [x] `#define` 단순 상수
- [x] `#define` 표현식 (`A * 2`)
- [x] `#define` 타입 별칭
- [x] `typedef` 기본형 별칭 (`BYTE`, `WORD`, `DWORD` 등)
- [x] `typedef struct`
- [x] `typedef enum`
- [x] `typedef` 함수 포인터
- [x] `const` 상수
- [x] `enum` 값 없음 (자동 증가)
- [x] `enum` 값 개별 지정 (예: `eFlowOne = 5`)
- [x] `enum` 혼합 (일부만 값 지정)
- [x] `struct` 기본
- [x] `struct` 중첩 멤버
- [x] `struct` 배열 멤버 `char name[64]`
- [x] `struct` 배열 멤버 `#define` 크기 `int data[MAX_SIZE]`
- [x] `struct` 익명 중첩 struct
- [x] `struct` 전방 선언
- [x] `union` 기본
- [x] `union` struct 내부 inline
- [x] 비트필드 `unsigned int flag : 1`
- [x] `#pragma pack(push, N)` / `#pragma pack(pop)`
- [x] `spare` / `reserved` / `dummy` 필드 (회색 표시)
- [x] 포인터 멤버 `int*`

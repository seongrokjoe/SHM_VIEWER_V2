// test_sample.h — SHM Viewer 검증용 샘플 헤더
// 원본 명세에서 수정된 사항:
//   1. KPoupInfo → KFoupInfo (타입오타)
//   2. struct JobSummary → struct tagJobSummary (이름 불일치)
//   3. typedef char STR80[80] 추가 (미정의 타입)
//   4. typedef long time_t 추가 (미정의 타입)
//   5. MAX_SLOT_COUNT → enum에 편입 (const int 파서 미지원 대비)
//   6. MAX_TEMP = 4 (원본 0, 배열 크기 안전값)

#pragma once

// ── 기본 타입 정의 ──────────────────────────────────────────────
typedef char STR80[80];
typedef long time_t;

// ── 상수 정의 ────────────────────────────────────────────────────
#define MAX_DSV_PRES_CNT 20

enum eMAX_UNIT
{
    MAX_TEMP       = 4,
    MAX_TEMP1      = 1,
    MAX_POINT      = 2,
    MAX_PORT       = 4,
	MAX_TMC = 6,
    MAX_DSV        = 12,
	MAX_PMC			= 16,
    MAX_CST_SLOT   = 26,
    MAX_SLOT_COUNT = 26,
	MAX_CTC_PIO = 1000,
};

// ── 구조체 정의 (의존 순서) ──────────────────────────────────────

struct KTempInfo
{
		long temp1;
		long temp2;
		long temp3;
};


struct KPortInfo
{
    STR80     carrier_id;
    long      job_seq;
    char      terminal_msg[80];
    KTempInfo tempInfo[2];      // fix: KPoupInfo → KFoupInfo
};


struct KFoupInfo
{
    long src_port;
    long des_port;
	KPortInfo portInfo[2];
};

struct tagCtc {
		long	cnn_host;
		long	cnn_amhs;
		long	cnn_stepper;
		
		long	cnn_pmc[MAX_PMC];
		long	cnn_tmc[MAX_TMC];
		
		long	alm_emg;
		long	alm_dis_only;
		
		char	io_value[MAX_CTC_PIO];
};

struct KSystem
{
    long alarm;
    char buzzer[6];
};

struct tagWaferSummary
{
    long temp[2];
};

struct tagJobSummary            // fix: was "struct JobSummary"
{
    tagWaferSummary sWaferSummary[MAX_SLOT_COUNT];
};

struct tagCp
{
    short         bake_temp;
    unsigned char recipe;
    char          spare1[10];
};

struct stDSVDcopInfo
{
    time_t         dateTime;
    unsigned short recipe;
    unsigned char  proc_step;
    time_t         s_time;
    time_t         e_time;
    short          sPres[MAX_DSV_PRES_CNT];
    long           StepTime;
};

struct tagDsv
{
    short         SetMass;
    stDSVDcopInfo DcopInfo;
};

struct tagDCOPData
{
    tagCp  Cp[1];
    tagDsv Dsv[MAX_DSV];
};

struct tagBsr
{
    short rpm_set;
    long  Recipe_id;
	KFoupInfo foupInfo[2];
};

struct KABCDSVRecipe
{
    long Number[MAX_TEMP1];
    long BevelSize[MAX_CST_SLOT][MAX_POINT];
    long DSVSize[MAX_CST_SLOT][MAX_POINT];
};

struct tagDynCfg
{
    long          sizetahDynCfg;
    KPortInfo     PortInfo[MAX_PORT];
	tagCtc		Ctc;
    KSystem       System;
    tagJobSummary JobSummary[MAX_PORT];
    tagDCOPData   DcopUnitData;
    tagBsr        Bsr[1];
    tagDsv        Dsv[MAX_DSV];
	tagBsr		  BsrTemp[2];
    KABCDSVRecipe memABCDSVRcp[1];
};

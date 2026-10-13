struct ConditionInfo
{
    int con_LampBuz[7];
    int other;
};

struct TowBuzInfoType
{
    ConditionInfo Condition[2];
};

struct Root
{
    TowBuzInfoType TowBuzInfo;
};

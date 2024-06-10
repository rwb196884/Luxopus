namespace Rwb.LxpBridge
{
    /// <summary>
    /// https://github.com/celsworth/lxp-bridge/wiki/Holdings
    /// </summary>
    internal static class Registers
    {
        public static Dictionary<byte, string> Key = new Dictionary<byte, string>()
        {
            {0,"HOLD_MODEL" },
            {1,"HOLD_MODEL" },
            {2,"HOLD_SERIAL_NUM" },
            {3,"HOLD_SERIAL_NUM" },
            {4,"HOLD_SERIAL_NUM" },
            {5,"HOLD_SERIAL_NUM" },
            {6,"HOLD_SERIAL_NUM" },
            {7,"HOLD_FW_CODE" },
            {8,"Unknown/Reserved" },
            {9,"Unknown/Reserved" },
            {10,"Unknown/Reserved" },
            {11,"Unknown/Reserved" },
            {12,"HOLD_TIME / year & month" },
            {13,"HOLD_TIME / hour & day" },
            {14,"HOLD_TIME / second & minute" },
            {15,"COM_ADDR" },
            {16,"LANGUAGE" },
            {19,"DEVICE_TYPE" },
            {20,"PV_INPUT_MODE" },
            {21,"FUNCTIONS(?)   Bitmask (see [Register 21](#register-21), below)" },
            {22,"START_PV_VOLT" },
            {23,"CONNECT_TIME" },
            {24,"RECONNECT_TIME" },
            {25,"GRID_VOLT_CONN_LOW" },
            {26,"GRID_VOLT_CONN_HIGH" },
            {27,"GRID_FREQ_CONN_LOW" },
            {28,"GRID_FREQ_CONN_HIGH" },
            {29,"GRID_VOLT_LIMIT1_LOW" },
            {30,"GRID_VOLT_LIMIT1_HIGH" },
            {31,"GRID_VOLT_LIMIT1_LOW_TIME" },
            {32,"GRID_VOLT_LIMIT1_HIGH_TIME" },
            {33,"GRID_VOLT_LIMIT2_LOW" },
            {34,"GRID_VOLT_LIMIT2_HIGH" },
            {35,"GRID_VOLT_LIMIT2_LOW_TIME" },
            {36,"GRID_VOLT_LIMIT2_HIGH_TIME" },
            {37,"GRID_VOLT_LIMIT3_LOW" },
            {38,"GRID_VOLT_LIMIT3_HIGH" },
            {39,"GRID_VOLT_LIMIT3_LOW_TIME" },
            {40,"GRID_VOLT_LIMIT3_HIGH_TIME" },
            {41,"GRID_VOLT_MOV_AVG_HIGH" },
            {42,"GRID_FREQ_LIMIT1_LOW" },
            {43,"GRID_FREQ_LIMIT1_HIGH" },
            {44,"GRID_FREQ_LIMIT1_LOW_TIME" },
            {45,"GRID_FREQ_LIMIT1_HIGH_TIME" },
            {46,"GRID_FREQ_LIMIT2_LOW" },
            {47,"GRID_FREQ_LIMIT2_HIGH" },
            {48,"GRID_FREQ_LIMIT2_LOW_TIME" },
            {49,"GRID_FREQ_LIMIT2_HIGH_TIME" },
            {50,"GRID_FREQ_LIMIT3_LOW" },
            {51,"GRID_FREQ_LIMIT3_HIGH" },
            {52,"GRID_FREQ_LIMIT3_LOW_TIME" },
            {53,"GRID_FREQ_LIMIT3_HIGH_TIME" },
            {54,"MAX_Q_PERCENT_FOR_QV" },
            {55,"V1L" },
            {56,"V2L" },
            {57,"V1H" },
            {58,"V2H" },
            {59,"REACTIVE_POWER_CMD_TYPE" },
            {60,"ACTIVE_POWER_PERCENT_CMD" },
            {61,"REACTIVE_POWER_PERCENT_CMD" },
            {62,"PF_CMD" },
            {63,"POWER_SOFT_START_SLOPE" },
            {64,"CHARGE_POWER_PERCENT_CMD" },
            {65,"DISCHG_POWER_PERCENT_CMD" },
            {66,"AC_CHARGE_POWER_CMD" },
            {67,"AC_CHARGE_SOC_LIMIT" },
            {68,"AC_CHARGE_START_HOUR / AC_CHARGE_START_MINUTE" },
            {69,"AC_CHARGE_END_HOUR / AC_CHARGE_END_MINUTE" },
            {70,"AC_CHARGE_START_HOUR_1 / AC_CHARGE_START_MINUTE_1" },
            {71,"AC_CHARGE_END_HOUR_1 / AC_CHARGE_END_MINUTE_1" },
            {72,"AC_CHARGE_START_HOUR_2 / AC_CHARGE_START_MINUTE_2" },
            {73,"AC_CHARGE_END_HOUR_2 / AC_CHARGE_END_MINUTE_2" },
            {74,"FORCED_CHG_POWER_CMD" },
            {75,"FORCED_CHG_SOC_LIMIT" },
            {76,"FORCED_CHARGE_START_HOUR / FORCED_CHARGE_START_MINUTE" },
            {77,"FORCED_CHARGE_END_HOUR/ FORCED_CHARGE_END_MINUTE" },
            {78,"FORCED_CHARGE_START_HOUR_1 / FORCED_CHARGE_START_MINUTE_1" },
            {79,"FORCED_CHARGE_END_HOUR_1 / FORCED_CHARGE_END_MINUTE_1" },
            {80,"FORCED_CHARGE_START_HOUR_2 / FORCED_CHARGE_START_MINUTE_2" },
            {81,"FORCED_CHARGE_END_HOUR_2 / FORCED_CHARGE_END_MINUTE_2" },
            {82,"FORCED_DISCHG_POWER_CMD" },
            {83,"FORCED_DISCHG_SOC_LIMIT" },
            {84,"FORCED_DISCHARGE_START_HOUR / FORCED_DISCHARGE_START_MINUTE" },
            {85,"FORCED_DISCHARGE_END_HOUR / FORCED_DISCHARGE_END_MINUTE" },
            {86,"FORCED_DISCHARGE_START_HOUR_1 / FORCED_DISCHARGE_START_MINUTE_1" },
            {87,"FORCED_DISCHARGE_END_HOUR_1 / FORCED_DISCHARGE_END_MINUTE_1" },
            {88,"FORCED_DISCHARGE_START_HOUR_2 / FORCED_DISCHARGE_START_MINUTE_2" },
            {89,"FORCED_DISCHARGE_END_HOUR_2 / FORCED_DISCHARGE_END_MINUTE_2" },
            {90,"EPS_VOLT_SET" },
            {91,"EPS_FREQ_SET" },
            {99,"LEAD_ACID_CHARGE_VOLT_REF" },
            {100,"LEAD_ACID_DISCHARGE_CUT_OFF_VOLT" },
            {101,"LEAD_ACID_CHARGE_RATE" },
            {102,"LEAD_ACID_DISCHARGE_RATE" },
            {103,"FEED_IN_GRID_POWER_PERCENT" },
            {105,"DISCHG_CUT_OFF_SOC_EOD" },
            {106,"LEAD_ACID_TEMPR_LOWER_LIMIT_DISCHG" },
            {107,"LEAD_ACID_TEMPR_UPPER_LIMIT_DISCHG" },
            {108,"LEAD_ACID_TEMPR_LOWER_LIMIT_CHG" },
            {109,"LEAD_ACID_TEMPR_UPPER_LIMIT_CHG" },
            {110,"FUNCTIONS_GRID(?)   Bitmask (see [Register 110](#register-110))" },
            {112,"SET_MASTER_OR_SLAVE" },
            {113,"SET_COMPOSED_PHASE" },
            {116,"P_TO_USER_START_DISCHG" },
            {118,"VBAT_START_DERATING" },
            {119,"This seems to be some sort of grid ct calibration offset to achieve true zero import/export. only seems to work in recent firmwares" },
            {120,"ST_SYS_ENABLE   Bitmask (see [Register 120](#register-120))" },
            {122,"MAINTENANCE_COUNT" },
            {125,"SOC_LOW_LIMIT_EPS_DISCHG" },
            {137,"SPEC_LOAD_COMPENSATE" },
            {144,"FLOATING_VOLTAGE" },
            {145,"OUTPUT_CONFIGURATION" },
            {146,"LINE_MODE_INPUT" },
            {147,"BATTERY_CAPACITY" },
            {148,"NOMINAL_BATTERY_VOLTAGE" },
            {149,"EQUALIZATION_VOLTAGE" },
            {150,"EQUALIZATION_PERIOD" },
            {151,"EQUALIZATION_TIME" },
            {152,"AC_FIRST_START_HOUR / AC_FIRST_START_MINUTE" },
            {153,"AC_FIRST_END_HOUR / AC_FIRST_END_MINUTE" },
            {154,"AC_FIRST_START_HOUR_1 / AC_FIRST_START_MINUTE_1" },
            {155,"AC_FIRST_END_HOUR_1 / AC_FIRST_END_MINUTE_1" },
            {156,"AC_FIRST_START_HOUR_2 / AC_FIRST_START_MINUTE_2" },
            {157,"AC_FIRST_END_HOUR_2 / AC_FIRST_END_MINUTE_2" },
            {158,"AC_CHARGE_START_BATTERY_VOLTAGE" },
            {159,"AC_CHARGE_END_BATTERY_VOLTAGE" },
            {160,"AC_CHARGE_START_BATTERY_SOC" },
            {161,"AC_CHARGE_END_BATTERY_SOC" },
            {162,"BATTERY_WARNING_VOLTAGE" },
            {163,"BATTERY_WARNING_RECOVERY_VOLTAGE" },
            {164,"BATTERY_WARNING_SOC" },
            {165,"BATTERY_WARNING_RECOVERY_SOC" },
            {166,"BATTERY_LOW_TO_UTILITY_VOLTAGE" },
            {167,"BATTERY_LOW_TO_UTILITY_SOC" },
            {168,"AC_CHARGE_BATTERY_CURRENT" },
            {169,"ON_GRID_EOD_VOLTAGE" },
            {177,"MAX_GENERATOR_INPUT_POWER" }
        };
    }

    public enum Register21
    {
        //0x8000 	Feed in grid
        //0x4000 	DCI
        //0x2000 	GFCI
        //0x1000 	? - can't see any effect, normally on?
        //0x0800 	Charge priority (charge before supplying load)
        //0x0400 	Forced discharge
        //0x0200 	Normal / standby(doesn't seem to save any power anyway)
        //0x0100 	Seamless EPS switching
        //0x0080 	AC Charge
        //0x0040 	Grid on power SS
        //0x0020 	Neutral detect
        //0x0010 	Anti island
        //0x0008 	? Can't see any effect. Normally off.
        //0x0004 	drms
        //0x0002 	ovf load derate
        //0x0001 	eps - setting fails with error code 3? (134 in dataframe second byte)
    }

    public enum Register110
    {
        //0x0004 	Micro grid enable
        //0x0002 	Fast zero export
        //0x0001 	PV without grid? (setting fails with error 3 (134 in dataframe second byte)    }
    }

    public enum Register120
    {

    }
}

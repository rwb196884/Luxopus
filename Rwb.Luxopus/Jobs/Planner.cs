﻿using Microsoft.Extensions.Logging;
using Rwb.Luxopus.Services;

namespace Rwb.Luxopus.Jobs
{
    public class HalfHourPlan : ElectricityPrice
    {
        // Base class: date, buy price, sell price.

        //public int PredictedGeneration { get; set; }

        public PeriodAction? Action { get; set; }

        //public PeriodState ExpectedStartState { get; set; }
        //public PeriodState ExpectedEndState { get; set; }

        //public PeriodState ActualStartState { get; set; }
        //public PeriodState ActualEndState { get; set; }

        public HalfHourPlan(ElectricityPrice e)
        {
            Start = e.Start;
            Buy = e.Buy;
            Sell = e.Sell;
            Action = null;
        }

        /// <summary>
        /// Required for System.Text.Json.JsonSerializer.Deserialize.
        /// </summary>
        /// <param name="plans"></param>
        public HalfHourPlan()
        {

        }
        public override string ToString()
        {
            return $"{base.ToString()} {Action?.ToString() ?? $"{PeriodAction.SpacesForToString} | {PeriodAction.SpacesForToString}"}";
        }
    }

    public class PeriodAction
    {
        /// <summary>
        /// Battery limit for charge. Use 0 to disable charge from grid.
        /// </summary>
        public int ChargeFromGrid { get; set; }

        /// <summary>
        /// If above zero then export generation to grid in preference to storing.
        /// </summary>
        //public int BatteryChargeRate { get; set; }

        /// <summary>
        /// Rate at which the battery should be discharged for use. Set to zero to force house to use grid.
        /// </summary>
        //public int BatterydDischargeRate { get; set; }

        /// <summary>
        /// Rate at which the battery should be discharged to the grid. 
        /// </summary>
        //public int BatteryGridDischargeRate { get; set; }

        /// <summary>
        /// Battery limit for discharge. Use 100 to disable discharge to grid.
        /// </summary>
        public int DischargeToGrid { get; set; }

        public PeriodAction()
        {
            ChargeFromGrid = 0;
            //BatteryChargeRate = 97;
            //BatterydDischargeRate = 97;
            //BatteryGridDischargeRate = 97;
            DischargeToGrid = 100;
        }

        public const string SpacesForToString = "      ";

        public override string ToString()
        {
            string chargeFromGrid = ChargeFromGrid > 0 ? $"{ChargeFromGrid:000}<--" : SpacesForToString;
            string dischargeTo = DischargeToGrid < 100 ? $"{DischargeToGrid:000}-->" : SpacesForToString;
            return $"{chargeFromGrid} | {dischargeTo}";// | ChargeRate {BatteryChargeRate} | DischargeRate {BatteryGridDischargeRate}";
        }
    }

    /// <summary>
    /// <para>
    /// Buy/sell strategy
    /// </para>
    /// <para>
    /// Probably heavily biassed to the UK market with lots of hard-coded assumptions.
    /// </para>
    /// </summary>
    public abstract class Planner : Job
    {
        protected readonly IInfluxQueryService InfluxQuery;
        protected readonly ILuxopusPlanService PlanService;

        public Planner(ILogger<LuxMonitor> logger, IInfluxQueryService influxQuery, ILuxopusPlanService planService) : base(logger)
        {
            InfluxQuery = influxQuery;
            PlanService = planService;
        }
    }
}

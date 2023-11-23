using System;

namespace Rwb.Luxopus.Services
{
    public enum ScaleMethod
    {
        Linear,
        FastLinear,
    }

    public static class Scale
    {
        public static int Apply(DateTime startTime, DateTime endTime, DateTime scaleTime,
            int startValue, int endValue, ScaleMethod method)
        {
            if( scaleTime <= startTime) { return startValue; }
            if( scaleTime >= endTime) { return endValue; }
            switch (method)
            {
                case (ScaleMethod.Linear):
                case (ScaleMethod.FastLinear):
                    double l_totalMinutes = endTime.Subtract(startTime).TotalMinutes;
                    double l_delta = Convert.ToDouble(endValue - startValue);
                    double l_deltaPerMinute = l_delta / l_totalMinutes;
                    double l_minutesToScaleTime = scaleTime.Subtract(startTime).TotalMinutes;
                    double l_scaleValue = startValue + l_deltaPerMinute * l_minutesToScaleTime;
                    return Convert.ToInt32(Math.Floor(l_scaleValue));
                //case (ScaleMethod.FastLinear):
                //    double f_totalMinutes = endTime.Subtract(startTime).TotalMinutes;
                //    double f_delta = Convert.ToDouble(endValue - startValue) * 1.5; // Extra factor.
                //    double f_deltaPerMinute = f_delta / f_totalMinutes;
                //    double f_minutesToScaleTime = scaleTime.Subtract(startTime).TotalMinutes;
                //    double f_scaleValue = startValue + f_deltaPerMinute * f_minutesToScaleTime;
                //    if( f_scaleValue > endValue) { return endValue; }
                //    return Convert.ToInt32(Math.Floor(f_scaleValue));

                default:
                    throw new NotImplementedException($"Scale method {method} is not implemented.");
            }
        }
    }
}

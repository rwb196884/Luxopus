using System;

namespace Rwb.Luxopus.Services
{
    public enum ScaleMethod
    {
        Linear,
        Fast,
        Slow,
    }

    public static class Scale
    {
        public static int Apply(DateTime startTime, DateTime endTime, DateTime scaleTime,
            int startValue, int endValue, ScaleMethod method)
        {
            if( scaleTime <= startTime) { return startValue; }
            if( scaleTime >= endTime) { return endValue; }

            decimal t = Convert.ToDecimal(scaleTime.Subtract(startTime).TotalSeconds) / Convert.ToDecimal(endTime.Subtract(startTime).TotalSeconds);
            decimal dy = Convert.ToDecimal(endValue - startValue);
            decimal linear = Convert.ToDecimal(startValue) + dy * t;
            decimal s = linear;

            switch (method)
            {
                case (ScaleMethod.Linear):
                    s = linear;
                    break;
                    //double l_totalMinutes = endTime.Subtract(startTime).TotalMinutes;
                    //double l_delta = Convert.ToDouble(endValue - startValue);
                    //double l_deltaPerMinute = l_delta / l_totalMinutes;
                    //double l_minutesToScaleTime = scaleTime.Subtract(startTime).TotalMinutes;
                    //double l_scaleValue = startValue + l_deltaPerMinute * l_minutesToScaleTime;
                //return Convert.ToInt32(Math.Floor(l_scaleValue));
                //case (ScaleMethod.FastLinear):
                //    double f_totalMinutes = endTime.Subtract(startTime).TotalMinutes;
                //    double f_delta = Convert.ToDouble(endValue - startValue) * 1.5; // Extra factor.
                //    double f_deltaPerMinute = f_delta / f_totalMinutes;
                //    double f_minutesToScaleTime = scaleTime.Subtract(startTime).TotalMinutes;
                //    double f_scaleValue = startValue + f_deltaPerMinute * f_minutesToScaleTime;
                //    if( f_scaleValue > endValue) { return endValue; }
                //    return Convert.ToInt32(Math.Floor(f_scaleValue));

                case (ScaleMethod.Fast):
                    s = startValue + dy * (-0.99m * t * t + 1.99m * t);
                    break;
                case (ScaleMethod.Slow):
                    //s = linear - 0.5m * (1.0m - t) * (linear - Convert.ToDecimal(startValue));
                    // y = axx + bx then a+b = 100 passes through (0,0) and (1, 100). Use a = 99
                    s = startValue + dy * (0.99m * t * t + 0.01m * t);
                    break;

                default:
                    throw new NotImplementedException($"Scale method {method} is not implemented.");
            }

            return Convert.ToInt32(Math.Ceiling(s));
        }
    }
}

namespace Rwb.Luxopus
{
    public static class Extensions
    {
        private static int RoundUp(this int toRound, int nearest)
        {
            if (toRound % nearest == 0) return toRound;
            return (nearest - toRound % nearest) + toRound;
        }
    }
}

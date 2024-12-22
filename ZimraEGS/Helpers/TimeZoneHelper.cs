namespace ZimraEGS.Helpers
{
    public static class TimeZoneHelper
    {
        private static readonly TimeZoneInfo SouthAfricaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");

        public static DateTimeOffset GetSouthAfricaTime()
        {
            //Convert UTC to South Africa Standard Time(UTC + 2)
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            DateTimeOffset southAfricaTime = TimeZoneInfo.ConvertTime(utcNow, SouthAfricaTimeZone);

            return southAfricaTime;
        }

        public static string GetSouthAfricaTimeString(string format = "yyyy-MM-ddTHH:mm:ss")
        {
            // Return the time as a string in the specified format
            return GetSouthAfricaTime().ToString(format);
        }
    }
}

using ZimraEGS.Services;

namespace ZimraEGS.Views.Account
{
    public static class Status
    {
        public static string Label(BillingState s) => s switch
        {
            BillingState.Free => "Free (test)",
            BillingState.Active => "Active",
            BillingState.EndingSoon => "Ending soon",
            BillingState.Grace => "In grace period",
            BillingState.Expired => "Ended",
            BillingState.NoAccount => "No account",
            _ => s.ToString()
        };
    }
}

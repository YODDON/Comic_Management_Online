namespace PaymentAPI.Settings
{
    /// <summary>
    /// Purchase recovery job (T23). The host bus is in-memory, so a crash or restart loses the saga messages in
    /// flight and leaves purchases stuck (Pending transaction, saga row in its last state). The job finds them and
    /// settles each one from the wallet ledger and the chapter unlock row. Environment variables:
    /// <c>PurchaseRecovery__Enabled</c>, <c>PurchaseRecovery__StuckAfterMinutes</c>, <c>PurchaseRecovery__IntervalMinutes</c>.
    /// </summary>
    public class PurchaseRecoverySettings
    {
        public const string SectionName = "PurchaseRecovery";

        /// <summary>Runs the job at startup and then every <see cref="IntervalMinutes"/>.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// A purchase is only touched when the transaction and the saga's last step are older than this. Must stay
        /// well above the purchase request timeout (30 s), so a saga that is still running is never settled.
        /// </summary>
        public int StuckAfterMinutes { get; set; } = 10;

        /// <summary>Minutes between two runs after the first one at startup. Values below 1 count as 1.</summary>
        public int IntervalMinutes { get; set; } = 5;
    }
}

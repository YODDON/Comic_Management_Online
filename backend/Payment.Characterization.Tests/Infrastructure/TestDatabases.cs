using ChapterAPI.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentAPI.Data;
using WalletAPI.Data;

namespace Payment.Characterization.Tests.Infrastructure;

/// <summary>
/// Three throwaway SQL Server databases (Payment, Wallet, Chapter), created with the REAL migrations so the
/// unique indexes and column types match production, and dropped when the test run ends.
/// Server: LocalDB by default; set T37_SQL_SERVER to use another instance (integrated security).
/// </summary>
public sealed class TestDatabases : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("T37_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public string PaymentName => $"T37_Payment_{_suffix}";
    public string WalletName => $"T37_Wallet_{_suffix}";
    public string ChapterName => $"T37_Chapter_{_suffix}";

    public string PaymentConnection => ConnectionFor(PaymentName);
    public string WalletConnection => ConnectionFor(WalletName);
    public string ChapterConnection => ConnectionFor(ChapterName);

    private static string ConnectionFor(string database) =>
        $"Server={Server};Database={database};Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=30";

    public async Task InitializeAsync()
    {
        using (var payment = new PaymentDbContext(new DbContextOptionsBuilder<PaymentDbContext>().UseSqlServer(PaymentConnection).Options))
            await payment.Database.MigrateAsync();
        using (var wallet = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(WalletConnection).Options))
            await wallet.Database.MigrateAsync();
        using (var chapter = new ChapterDbContext(new DbContextOptionsBuilder<ChapterDbContext>().UseSqlServer(ChapterConnection).Options))
            await chapter.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection($"Server={Server};Database=master;Trusted_Connection=True;TrustServerCertificate=True");
        await master.OpenAsync();
        foreach (var name in new[] { PaymentName, WalletName, ChapterName })
        {
            await using var cmd = master.CreateCommand();
            // Names are generated above (letters, digits, underscore), never user input.
            cmd.CommandText =
                $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

using System.Text;
using Microsoft.Extensions.Hosting;

namespace ComicMonolith.Api;

/// <summary>
/// Fail-fast checks that run once at host startup, before any module is registered.
/// Host-only on purpose: the modules keep their own lookup so the standalone APIs still work.
/// </summary>
public static class StartupChecks
{
    /// <summary>One module's database connection: the env var and config keys it reads, in the order it reads them.</summary>
    public sealed record ModuleConnection(string Module, string EnvVar, params string[] ConnectionStringNames);

    /// <summary>
    /// Same lookup order as each module (`AddXxxModule`): env var, then `ConnectionStrings:{name}` for each name.
    /// Keep this list in sync with the modules until T12's configuration merge removes the per-module lookups.
    /// </summary>
    public static readonly IReadOnlyList<ModuleConnection> Modules =
    [
        new("Banner", "BANNER_DB_CONNECTION", "BannerConnection", "DefaultConnection"),
        new("Chapter", "CHAPTER_DB_CONNECTION", "ChapterConnection"),
        new("Comic", "COMIC_DB_CONNECTION", "ComicConnection"),
        new("Mission", "MISSION_DB_CONNECTION", "MissionConnection"),
        new("Payment", "PAYMENT_DB_CONNECTION", "PaymentConnection"),
        new("Social", "SOCIAL_DB_CONNECTION", "SocialConnection"),
        new("User", "AUTH_DB_CONNECTION", "AuthConnection"),
        new("Wallet", "WALLET_DB_CONNECTION", "WalletConnection", "DefaultConnection"),
    ];

    /// <summary>Minimum JWT secret size in bytes (UTF-8).</summary>
    public const int MinJwtSecretBytes = 32;

    /// <summary>
    /// Throws one <see cref="InvalidOperationException"/> that lists every module without a connection string.
    /// Runs in every environment. Connection string values are never put in the message.
    /// </summary>
    public static void ValidateConnectionStrings(IConfiguration configuration, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var missing = new List<string>();
        foreach (var module in Modules)
        {
            // Same `??` chain and same IsNullOrEmpty test as the modules.
            var value = getEnv(module.EnvVar);
            foreach (var name in module.ConnectionStringNames)
            {
                value ??= configuration.GetConnectionString(name);
            }

            if (string.IsNullOrEmpty(value))
            {
                var keys = string.Join(" or ", module.ConnectionStringNames.Select(n => $"ConnectionStrings:{n}"));
                missing.Add($"{module.Module}: env {module.EnvVar} or config {keys}");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Missing database connection string for " + missing.Count + " module(s). Set each one:"
                + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", missing));
        }
    }

    /// <summary>
    /// Outside Development (an unset ASPNETCORE_ENVIRONMENT counts as Production), throws if the JWT secret
    /// is missing or shorter than <see cref="MinJwtSecretBytes"/> bytes. In Development it returns a loud
    /// warning text instead (the shared JWT code then falls back to its built-in development default),
    /// or null when the secret is fine. The secret's value is never put in a message.
    /// </summary>
    public static string? EnforceJwtSecret(IConfiguration configuration, IHostEnvironment environment, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        // Same lookup order as SharedKernel's AddCustomJwtAuthentication. Issuer, audience, signing,
        // lifetime and ClockSkew are not touched here.
        var secret = getEnv("JwtSettings__Secret") ?? getEnv("JWT_SECRET") ?? configuration["JwtSettings:Secret"];

        string? problem = null;
        if (string.IsNullOrEmpty(secret))
        {
            problem = "missing";
        }
        else if (Encoding.UTF8.GetByteCount(secret) < MinJwtSecretBytes)
        {
            problem = "too short";
        }

        if (problem is null)
        {
            return null;
        }

        if (environment.IsDevelopment())
        {
            return $"INSECURE JWT SECRET: JwtSettings__Secret (or JWT_SECRET) is {problem}. "
                + "Allowed only because the environment is Development; do not run like this anywhere else.";
        }

        throw new InvalidOperationException(
            $"JwtSettings__Secret (or its alias JWT_SECRET) is {problem} (minimum {MinJwtSecretBytes} bytes). "
            + $"Refusing to start in the {environment.EnvironmentName} environment.");
    }
}

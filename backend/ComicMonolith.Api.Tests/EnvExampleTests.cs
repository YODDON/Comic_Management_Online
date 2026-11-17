using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ComicMonolith.Api.Tests;

/// <summary>
/// Guards `.env.example` against drifting from the startup checks. The file is found by walking up from the
/// test assembly to the folder that contains the .sln.
/// </summary>
public class EnvExampleTests
{
    private const string SolutionFile = "prn232_comic_api.sln";

    private static string FindEnvExample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
            {
                var path = Path.Combine(dir.FullName, ".env.example");
                Assert.True(File.Exists(path), $"Found {SolutionFile} in '{dir.FullName}' but no .env.example next to it.");
                return path;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find {SolutionFile} by walking up from '{AppContext.BaseDirectory}'. "
            + "The drift tests need the repository layout (backend/.env.example next to the .sln).");
    }

    /// <summary>Active `KEY=value` lines only; comments (including commented-out aliases) are ignored.</summary>
    private static Dictionary<string, string> ActiveSettings()
    {
        var result = new Dictionary<string, string>();
        foreach (var raw in File.ReadAllLines(FindEnvExample()))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq]] = line[(eq + 1)..];
        }

        return result;
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Every_connection_string_the_host_requires_is_listed()
    {
        var active = ActiveSettings();

        foreach (var module in StartupChecks.Modules)
        {
            Assert.True(active.ContainsKey(module.EnvVar), $".env.example is missing {module.EnvVar} ({module.Module} module).");
            Assert.False(string.IsNullOrWhiteSpace(active[module.EnvVar]), $"{module.EnvVar} has no example value.");
        }
    }

    [Fact]
    public void The_JWT_variables_are_listed()
    {
        var active = ActiveSettings();

        foreach (var key in new[] { "JwtSettings__Secret", "JwtSettings__Issuer", "JwtSettings__Audience", "JwtSettings__ExpiryMinutes" })
        {
            Assert.True(active.ContainsKey(key), $".env.example is missing {key}.");
        }
    }

    [Fact]
    public void The_example_JWT_secret_trips_the_fail_fast_outside_Development()
    {
        var secret = ActiveSettings()["JwtSettings__Secret"];
        var config = new ConfigurationBuilder().Build();

        // Copying the example file unchanged must not be enough to start in Production.
        Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.EnforceJwtSecret(
                config,
                new FakeEnvironment("Production"),
                name => name == "JwtSettings__Secret" ? secret : null));
    }

    [Fact]
    public void The_example_file_holds_only_placeholder_secrets()
    {
        var active = ActiveSettings();

        foreach (var key in new[] { "JwtSettings__Secret", "Cloudinary__ApiSecret", "SMTP_PASS", "SQL_SA_PASSWORD", "BankSettings__SepayApiKey" })
        {
            Assert.True(active.TryGetValue(key, out var value), $".env.example is missing {key}.");
            Assert.Equal("CHANGE_ME", value);
        }
    }

    [Fact]
    public void The_purchase_recovery_variables_match_the_code_defaults()
    {
        var active = ActiveSettings();
        var defaults = new PaymentAPI.Settings.PurchaseRecoverySettings();

        Assert.Equal(defaults.Enabled.ToString().ToLowerInvariant(), active["PurchaseRecovery__Enabled"]);
        Assert.Equal(defaults.StuckAfterMinutes.ToString(), active["PurchaseRecovery__StuckAfterMinutes"]);
        Assert.Equal(defaults.IntervalMinutes.ToString(), active["PurchaseRecovery__IntervalMinutes"]);
    }
}

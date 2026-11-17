using Microsoft.Extensions.Configuration;

namespace ComicMonolith.Api.Tests;

public class ConnectionStringCheckTests
{
    private static readonly string[] AllEnvVars =
    [
        "BANNER_DB_CONNECTION", "CHAPTER_DB_CONNECTION", "COMIC_DB_CONNECTION", "MISSION_DB_CONNECTION",
        "PAYMENT_DB_CONNECTION", "SOCIAL_DB_CONNECTION", "AUTH_DB_CONNECTION", "WALLET_DB_CONNECTION",
    ];

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static Func<string, string?> Env(IDictionary<string, string> values) =>
        name => values.TryGetValue(name, out var v) ? v : null;

    private static Dictionary<string, string> AllEnvSet() =>
        AllEnvVars.ToDictionary(n => n, n => "Server=fake;Database=" + n);

    [Fact]
    public void Throws_and_lists_every_module_when_all_are_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.ValidateConnectionStrings(Config(), Env(new Dictionary<string, string>())));

        foreach (var envVar in AllEnvVars)
        {
            Assert.Contains(envVar, ex.Message);
        }

        Assert.Contains("ConnectionStrings:BannerConnection", ex.Message);
        Assert.Contains("ConnectionStrings:ChapterConnection", ex.Message);
        Assert.Contains("ConnectionStrings:ComicConnection", ex.Message);
        Assert.Contains("ConnectionStrings:MissionConnection", ex.Message);
        Assert.Contains("ConnectionStrings:PaymentConnection", ex.Message);
        Assert.Contains("ConnectionStrings:SocialConnection", ex.Message);
        Assert.Contains("ConnectionStrings:AuthConnection", ex.Message);
        Assert.Contains("ConnectionStrings:WalletConnection", ex.Message);
        Assert.Contains("8 module(s)", ex.Message);
    }

    [Fact]
    public void Passes_when_every_env_var_is_set()
    {
        StartupChecks.ValidateConnectionStrings(Config(), Env(AllEnvSet()));
    }

    [Fact]
    public void Passes_when_every_module_uses_its_config_key()
    {
        var config = Config(
            ("ConnectionStrings:BannerConnection", "x"),
            ("ConnectionStrings:ChapterConnection", "x"),
            ("ConnectionStrings:ComicConnection", "x"),
            ("ConnectionStrings:MissionConnection", "x"),
            ("ConnectionStrings:PaymentConnection", "x"),
            ("ConnectionStrings:SocialConnection", "x"),
            ("ConnectionStrings:AuthConnection", "x"),
            ("ConnectionStrings:WalletConnection", "x"));

        StartupChecks.ValidateConnectionStrings(config, Env(new Dictionary<string, string>()));
    }

    [Fact]
    public void Reports_only_the_module_that_is_missing()
    {
        var env = AllEnvSet();
        env.Remove("COMIC_DB_CONNECTION");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.ValidateConnectionStrings(Config(), Env(env)));

        Assert.Contains("1 module(s)", ex.Message);
        Assert.Contains("COMIC_DB_CONNECTION", ex.Message);
        Assert.Contains("ConnectionStrings:ComicConnection", ex.Message);
        Assert.DoesNotContain("BANNER_DB_CONNECTION", ex.Message);
        Assert.DoesNotContain("PAYMENT_DB_CONNECTION", ex.Message);
    }

    [Fact]
    public void Banner_and_Wallet_fall_back_to_DefaultConnection()
    {
        var env = AllEnvSet();
        env.Remove("BANNER_DB_CONNECTION");
        env.Remove("WALLET_DB_CONNECTION");

        StartupChecks.ValidateConnectionStrings(Config(("ConnectionStrings:DefaultConnection", "x")), Env(env));
    }

    [Fact]
    public void Banner_and_Wallet_message_names_the_DefaultConnection_fallback()
    {
        var env = AllEnvSet();
        env.Remove("BANNER_DB_CONNECTION");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.ValidateConnectionStrings(Config(), Env(env)));

        Assert.Contains("ConnectionStrings:BannerConnection or ConnectionStrings:DefaultConnection", ex.Message);
    }

    [Fact]
    public void Other_modules_do_not_fall_back_to_DefaultConnection()
    {
        var env = AllEnvSet();
        env.Remove("COMIC_DB_CONNECTION");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.ValidateConnectionStrings(Config(("ConnectionStrings:DefaultConnection", "x")), Env(env)));

        Assert.Contains("COMIC_DB_CONNECTION", ex.Message);
    }

    [Fact]
    public void An_empty_env_var_counts_as_missing()
    {
        var env = AllEnvSet();
        env["SOCIAL_DB_CONNECTION"] = "";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.ValidateConnectionStrings(Config(), Env(env)));

        Assert.Contains("SOCIAL_DB_CONNECTION", ex.Message);
    }

    [Fact]
    public void The_message_never_contains_a_connection_string_value()
    {
        var env = AllEnvSet();
        env["BANNER_DB_CONNECTION"] = "Server=leak.example;Password=hunter2";
        env.Remove("COMIC_DB_CONNECTION");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.ValidateConnectionStrings(Config(), Env(env)));

        Assert.DoesNotContain("leak.example", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message);
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ComicMonolith.Api.Tests;

// Tests that touch process-wide environment variables must not run in parallel with each other.
[CollectionDefinition("process-environment", DisableParallelization = true)]
public class ProcessEnvironmentCollection;

[Collection("process-environment")]
public class JwtSecretCheckTests
{
    private const string Secret32 = "0123456789abcdef0123456789abcdef"; // exactly 32 bytes
    private const string Secret31 = "0123456789abcdef0123456789abcde"; // 31 bytes

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(string? secret = null)
    {
        var values = new Dictionary<string, string?>();
        if (secret is not null) values["JwtSettings:Secret"] = secret;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    // ---- Non-Development: throws on missing / short, passes on ok ----

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Throws_when_secret_is_unset_outside_Development(string environmentName)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment(environmentName), Env()));

        Assert.Contains("JwtSettings__Secret", ex.Message);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Throws_when_secret_is_31_bytes_in_Production()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Production"), Env(("JwtSettings__Secret", Secret31))));

        Assert.Contains("too short", ex.Message);
        Assert.DoesNotContain(Secret31, ex.Message);
    }

    [Fact]
    public void Passes_when_secret_is_exactly_32_bytes_in_Production()
    {
        var result = StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Production"), Env(("JwtSettings__Secret", Secret32)));

        Assert.Null(result);
    }

    [Fact]
    public void Length_is_counted_in_bytes_not_characters()
    {
        // 16 two-byte characters = 32 bytes, 16 characters.
        var twoByte = new string('é', 16);

        Assert.Null(StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Production"), Env(("JwtSettings__Secret", twoByte))));
    }

    [Fact]
    public void The_JWT_SECRET_alias_is_accepted()
    {
        var result = StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Production"), Env(("JWT_SECRET", Secret32)));

        Assert.Null(result);
    }

    [Fact]
    public void JwtSettings__Secret_takes_priority_over_the_alias()
    {
        // Same order as SharedKernel: a short primary value is used even when the alias is long enough.
        Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.EnforceJwtSecret(
                Config(),
                new FakeEnvironment("Production"),
                Env(("JwtSettings__Secret", Secret31), ("JWT_SECRET", Secret32))));
    }

    [Fact]
    public void The_appsettings_key_is_accepted()
    {
        var result = StartupChecks.EnforceJwtSecret(Config(Secret32), new FakeEnvironment("Production"), Env());

        Assert.Null(result);
    }

    [Fact]
    public void An_empty_secret_counts_as_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Production"), Env(("JwtSettings__Secret", ""))));

        Assert.Contains("missing", ex.Message);
    }

    // ---- Development: loud warning, never throws ----

    [Fact]
    public void Development_with_unset_secret_returns_a_loud_warning()
    {
        var warning = StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Development"), Env());

        Assert.NotNull(warning);
        Assert.Contains("INSECURE JWT SECRET", warning);
        Assert.Contains("missing", warning);
        Assert.Contains("Development", warning);
    }

    [Fact]
    public void Development_with_short_secret_warns_without_revealing_the_value()
    {
        const string shortSecret = "hunter2-short";

        var warning = StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Development"), Env(("JwtSettings__Secret", shortSecret)));

        Assert.NotNull(warning);
        Assert.Contains("too short", warning);
        Assert.DoesNotContain(shortSecret, warning);
    }

    [Fact]
    public void Development_with_a_good_secret_returns_no_warning()
    {
        Assert.Null(StartupChecks.EnforceJwtSecret(Config(), new FakeEnvironment("Development"), Env(("JwtSettings__Secret", Secret32))));
    }

    // ---- The .NET default environment ----

    [Fact]
    public void Unset_ASPNETCORE_ENVIRONMENT_is_treated_as_Production()
    {
        var saved = (
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);

            var builder = WebApplication.CreateBuilder();

            Assert.False(builder.Environment.IsDevelopment());
            Assert.Equal(Environments.Production, builder.Environment.EnvironmentName);
            Assert.Throws<InvalidOperationException>(() =>
                StartupChecks.EnforceJwtSecret(Config(), builder.Environment, Env()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", saved.Item1);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", saved.Item2);
        }
    }
}

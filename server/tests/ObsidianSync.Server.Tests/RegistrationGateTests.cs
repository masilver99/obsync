using Microsoft.Extensions.Configuration;
using ObsidianSync.Server.Security;
using Xunit;

namespace ObsidianSync.Server.Tests;

public sealed class RegistrationGateTests
{
    [Fact]
    public void MissingKeyDisablesRegistration()
    {
        var configuration = new ConfigurationBuilder().Build();
        var gate = new RegistrationGate(configuration);

        Assert.False(gate.IsEnabled);
        Assert.False(gate.IsValid("anything"));
    }

    [Fact]
    public void ConfiguredKeyMustMatchExactly()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REGISTRATION_KEY"] = "invite-only-secret"
            })
            .Build();
        var gate = new RegistrationGate(configuration);

        Assert.True(gate.IsEnabled);
        Assert.True(gate.IsValid("invite-only-secret"));
        Assert.False(gate.IsValid("wrong-secret"));
        Assert.False(gate.IsValid(null));
    }
}

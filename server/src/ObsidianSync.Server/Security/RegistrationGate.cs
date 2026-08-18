using System.Security.Cryptography;
using System.Text;

namespace ObsidianSync.Server.Security;

public sealed class RegistrationGate(IConfiguration configuration)
{
    private readonly string? _registrationKey = configuration["REGISTRATION_KEY"] ?? configuration["Registration:Key"];

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_registrationKey);

    public bool IsValid(string? suppliedKey)
    {
        if (!IsEnabled || string.IsNullOrEmpty(suppliedKey))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(_registrationKey!);
        var supplied = Encoding.UTF8.GetBytes(suppliedKey);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}

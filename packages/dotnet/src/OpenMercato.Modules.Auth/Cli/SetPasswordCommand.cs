using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Validators;

namespace OpenMercato.Modules.Auth.Cli;

/// <summary>
/// <c>set-password</c> — port of upstream auth cli.ts setPassword. Resets a user's bcrypt password,
/// resolving the user by the deterministic email lookup hash (email is encrypted at rest).
/// Usage: set-password --email &lt;email&gt; --password &lt;newPassword&gt;
/// </summary>
public sealed class SetPasswordCommand : ICliCommand
{
    public string Name => "set-password";
    public string Description => "Reset a user's password: --email --password";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        var email = parsed.Get("email");
        var password = parsed.Get("password");
        if (email is null || password is null)
        {
            Console.Error.WriteLine("Usage: set-password --email <email> --password <newPassword>");
            return 1;
        }
        if (!PasswordPolicy.IsValid(password))
        {
            Console.Error.WriteLine(PasswordPolicy.Message);
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        var lookup = encryption.EmailHashLookupValues(email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.EmailHash != null && lookup.Contains(u.EmailHash));
        if (user is null)
        {
            Console.Error.WriteLine($"User with email \"{email}\" not found");
            return 1;
        }

        user.PasswordHash = hasher.Hash(password);
        await db.SaveChangesAsync();

        Console.WriteLine($"Password updated successfully for user: {email}");
        return 0;
    }
}

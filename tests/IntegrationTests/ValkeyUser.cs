using System.Text.RegularExpressions;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>
/// A service's Valkey user exactly as OpenTofu declares it (infra/env/valkey.tf): its access string, and
/// the client options its connection string carries.
/// </summary>
internal static partial class ValkeyUser
{
    private static readonly string Declared = File.ReadAllText(Path.Combine(Root(), "infra", "env", "valkey.tf"));

    public static string AccessString(string user) =>
        AccessStrings().Matches(Declared).Single(m => m.Groups["user"].Value == user).Groups["access"].Value;

    public static bool IsRestricted(string user) =>
        RestrictedUsers().Match(Declared).Groups["users"].Value.Contains($"\"{user}\"", StringComparison.Ordinal);

    /// <summary>What the secret appends for a user limited to its own keys.</summary>
    public static string RestrictedOptions => RestrictedOptionsLine().Match(Declared).Groups["options"].Value;

    /// <summary>Creates the user on a Valkey where <paramref name="admin"/> may run ACL SETUSER.</summary>
    public static async Task Create(IConnectionMultiplexer admin, string user, string password) =>
        await admin.GetDatabase().ExecuteAsync("ACL", ["SETUSER", user, "reset", .. AccessString(user).Split(' '), ">" + password]);

    /// <summary>The connection string the user's secret holds, without TLS.</summary>
    public static string ConnectionString(string address, string user, string password) =>
        $"{address},abortConnect=false,user={user},password={password}{(IsRestricted(user) ? RestrictedOptions : "")}";

    private static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CrowdFunding.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return root;
    }

    [GeneratedRegex(@"^\s*(?<user>[a-z]+)\s*=\s*""(?<access>on [^""]+)""", RegexOptions.Multiline)]
    private static partial Regex AccessStrings();

    [GeneratedRegex(@"valkey_restricted_users\s*=\s*toset\(\[(?<users>[^\]]*)\]\)")]
    private static partial Regex RestrictedUsers();

    [GeneratedRegex(@"valkey_restricted_options\s*=\s*""(?<options>[^""]*)""")]
    private static partial Regex RestrictedOptionsLine();
}

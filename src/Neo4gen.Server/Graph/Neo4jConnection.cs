using Neo4j.Driver;

namespace Neo4gen.Server.Graph;

// Parsed pieces of a single Neo4j connection string. The .NET driver wants a
// credential-free URI plus an auth token, so we split those out of one string of the form:
//   neo4j+s://<user>:<password>@<host>[:<port>][/<database>]
// (URL-encode the password if it contains reserved characters like @ : / ?)
// Database is null when the string omits it — the driver then uses the instance's home database.
public sealed record Neo4jConnection(string Uri, string Username, string Password, string? Database)
{
    public IAuthToken AuthToken => AuthTokens.Basic(Username, Password);

    public static Neo4jConnection Parse(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Neo4j connection string is empty.", nameof(connectionString));
        }

        var uri = new System.Uri(connectionString);

        var userInfo = uri.UserInfo.Split(':', 2);
        var username = userInfo.Length > 0 && userInfo[0].Length > 0
            ? System.Uri.UnescapeDataString(userInfo[0])
            : "neo4j";
        var password = userInfo.Length > 1 ? System.Uri.UnescapeDataString(userInfo[1]) : string.Empty;

        // Rebuild the URI without the userinfo — the driver takes credentials separately.
        var port = uri.Port >= 0 ? $":{uri.Port}" : string.Empty;
        var driverUri = $"{uri.Scheme}://{uri.Host}{port}";

        var database = uri.AbsolutePath.Trim('/');

        return new Neo4jConnection(
            driverUri,
            username,
            password,
            string.IsNullOrEmpty(database) ? null : database);
    }
}

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentHub.Server.Data;

namespace AgentHub.Server.Routes;

public static partial class AdminRoutes
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,62}$")]
    private static partial Regex AgentIdRegex();

    public static void MapAdminRoutes(this WebApplication app, string adminKey)
    {
        app.MapPost("/api/admin/agents", (HttpContext ctx, Database db, CreateAgentRequest req) =>
        {
            // Admin key auth
            var bearer = ExtractBearer(ctx.Request);
            if (string.IsNullOrEmpty(bearer) || bearer != adminKey)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            if (string.IsNullOrEmpty(req.Id))
                return Results.Json(new { error = "id is required" }, statusCode: 400);

            if (db.GetAgentById(req.Id) != null)
                return Results.Json(new { error = "agent already exists" }, statusCode: 409);

            var apiKey = GenerateApiKey();
            db.CreateAgent(req.Id, apiKey);

            return Results.Json(new { id = req.Id, api_key = apiKey }, statusCode: 201);
        });

        app.MapPost("/api/register", (HttpContext ctx, Database db, CreateAgentRequest req) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!db.CheckRateLimit($"ip:{ip}", "register", 10))
                return Results.Json(new { error = "registration rate limit exceeded" }, statusCode: 429);

            if (!AgentIdRegex().IsMatch(req.Id ?? ""))
                return Results.Json(new { error = "id must be 1-63 chars, alphanumeric/dash/dot/underscore, start with alphanumeric" }, statusCode: 400);

            if (db.GetAgentById(req.Id!) != null)
                return Results.Json(new { error = "agent id already taken" }, statusCode: 409);

            var apiKey = GenerateApiKey();
            db.CreateAgent(req.Id!, apiKey);
            db.IncrementRateLimit($"ip:{ip}", "register");

            return Results.Json(new { id = req.Id, api_key = apiKey }, statusCode: 201);
        });
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexStringLower(bytes);
    }

    private static string? ExtractBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header[7..];
        return null;
    }
}

public record CreateAgentRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id);

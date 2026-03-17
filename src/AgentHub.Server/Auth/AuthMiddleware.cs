using AgentHub.Server.Models;

namespace AgentHub.Server.Auth;

public static class AuthExtensions
{
    private const string AgentContextKey = "agent";

    public static Agent? GetAgent(this HttpContext context)
    {
        return context.Items[AgentContextKey] as Agent;
    }

    public static void SetAgent(this HttpContext context, Agent agent)
    {
        context.Items[AgentContextKey] = agent;
    }
}

public class AgentAuthMiddleware
{
    private readonly RequestDelegate _next;

    public AgentAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, Data.Database db)
    {
        var key = ExtractBearer(context.Request);
        if (string.IsNullOrEmpty(key))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"missing authorization\"}");
            return;
        }

        var agent = db.GetAgentByApiKey(key);
        if (agent == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"invalid api key\"}");
            return;
        }

        context.SetAgent(agent);
        await _next(context);
    }

    private static string? ExtractBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header[7..];
        return null;
    }
}

using AgentHub.Server.Auth;

namespace AgentHub.Server.Routes;

/// <summary>Extension methods for requiring agent authentication on route endpoints.</summary>
public static class RouteAuthExtensions
{
    /// <summary>
    /// Adds agent authentication middleware to a route endpoint using a filter.
    /// Endpoints marked with this will reject requests without a valid Bearer token.
    /// </summary>
    public static TBuilder RequireAgentAuth<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<Data.Database>();
            var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
            string? key = null;
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                key = header[7..];

            if (string.IsNullOrEmpty(key))
                return Results.Json(new { error = "missing authorization" }, statusCode: 401);

            var agent = db.GetAgentByApiKey(key);
            if (agent == null)
                return Results.Json(new { error = "invalid api key" }, statusCode: 401);

            ctx.HttpContext.SetAgent(agent);
            return await next(ctx);
        });
    }
}

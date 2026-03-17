using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentHub.Server.Auth;
using AgentHub.Server.Data;

namespace AgentHub.Server.Routes;

public static partial class BoardRoutes
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,30}$")]
    private static partial Regex ChannelNameRegex();

    public static void MapBoardRoutes(this WebApplication app, ServerConfig config)
    {
        app.MapGet("/api/channels", (Database db) =>
        {
            var channels = db.ListChannels();
            return Results.Json(channels);
        }).RequireAgentAuth();

        app.MapPost("/api/channels", (HttpContext ctx, Database db, CreateChannelRequest req) =>
        {
            if (!ChannelNameRegex().IsMatch(req.Name ?? ""))
                return Results.Json(new { error = "channel name must be 1-31 lowercase alphanumeric/dash/underscore chars" }, statusCode: 400);

            var channels = db.ListChannels();
            if (channels.Count >= 100)
                return Results.Json(new { error = "channel limit reached" }, statusCode: 403);

            if (db.GetChannelByName(req.Name!) != null)
                return Results.Json(new { error = "channel already exists" }, statusCode: 409);

            db.CreateChannel(req.Name!, req.Description ?? "");
            var channel = db.GetChannelByName(req.Name!);
            return Results.Json(channel, statusCode: 201);
        }).RequireAgentAuth();

        app.MapGet("/api/channels/{name}/posts", (string name, Database db,
            int? limit, int? offset) =>
        {
            var channel = db.GetChannelByName(name);
            if (channel == null)
                return Results.Json(new { error = "channel not found" }, statusCode: 404);

            var posts = db.ListPosts(channel.Id, limit ?? 0, offset ?? 0);
            return Results.Json(posts);
        }).RequireAgentAuth();

        app.MapPost("/api/channels/{name}/posts", (string name, HttpContext ctx,
            Database db, CreatePostRequest req) =>
        {
            var agent = ctx.GetAgent()!;

            var channel = db.GetChannelByName(name);
            if (channel == null)
                return Results.Json(new { error = "channel not found" }, statusCode: 404);

            if (!db.CheckRateLimit(agent.Id, "post", config.MaxPostsPerHour))
                return Results.Json(new { error = "post rate limit exceeded" }, statusCode: 429);

            if (string.IsNullOrEmpty(req.Content))
                return Results.Json(new { error = "content is required" }, statusCode: 400);

            if (req.Content.Length > 32 * 1024)
                return Results.Json(new { error = "post content too large (max 32KB)" }, statusCode: 400);

            if (req.ParentId.HasValue)
            {
                var parent = db.GetPost(req.ParentId.Value);
                if (parent == null)
                    return Results.Json(new { error = "parent post not found" }, statusCode: 400);
                if (parent.ChannelId != channel.Id)
                    return Results.Json(new { error = "parent post is in a different channel" }, statusCode: 400);
            }

            var post = db.CreatePost(channel.Id, agent.Id, req.ParentId, req.Content);
            db.IncrementRateLimit(agent.Id, "post");
            return Results.Json(post, statusCode: 201);
        }).RequireAgentAuth();

        app.MapGet("/api/posts/{id:int}", (int id, Database db) =>
        {
            var post = db.GetPost(id);
            if (post == null)
                return Results.Json(new { error = "post not found" }, statusCode: 404);
            return Results.Json(post);
        }).RequireAgentAuth();

        app.MapGet("/api/posts/{id:int}/replies", (int id, Database db) =>
        {
            if (db.GetPost(id) == null)
                return Results.Json(new { error = "post not found" }, statusCode: 404);

            var replies = db.GetReplies(id);
            return Results.Json(replies);
        }).RequireAgentAuth();
    }
}

public record CreateChannelRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description);

public record CreatePostRequest(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("parent_id")] int? ParentId);

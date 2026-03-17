using AgentHub.Server.Auth;
using AgentHub.Server.Data;
using AgentHub.Server.GitRepo;
using AgentHub.Server.Models;

namespace AgentHub.Server.Routes;

public static class GitRoutes
{
    public static void MapGitRoutes(this WebApplication app, ServerConfig config)
    {
        app.MapPost("/api/git/push", async (HttpContext ctx, Database db, GitRepository repo) =>
        {
            var agent = ctx.GetAgent()!;

            if (!db.CheckRateLimit(agent.Id, "push", config.MaxPushesPerHour))
                return Results.Json(new { error = "push rate limit exceeded" }, statusCode: 429);

            // Read bundle with size limit
            if (ctx.Request.ContentLength > config.MaxBundleSize)
                return Results.Json(new { error = "bundle too large" }, statusCode: 413);

            // Read body, enforcing size limit
            byte[] bodyBytes;
            try
            {
                bodyBytes = await ReadBodyWithLimitAsync(ctx.Request.Body, config.MaxBundleSize);
            }
            catch (InvalidOperationException)
            {
                return Results.Json(new { error = "bundle too large" }, statusCode: 413);
            }

            var tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arhub-push-{Guid.NewGuid():N}.bundle");
            try
            {
                await File.WriteAllBytesAsync(tmpFile, bodyBytes);

                string[] hashes;
                try
                {
                    hashes = await repo.UnbundleAsync(tmpFile);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = $"invalid bundle: {ex.Message}" }, statusCode: 400);
                }

                var indexed = new List<string>();
                foreach (var hash in hashes)
                {
                    var existing = db.GetCommit(hash);
                    if (existing != null)
                    {
                        indexed.Add(hash);
                        continue;
                    }

                    var (parentHash, message) = await repo.GetCommitInfoAsync(hash);

                    if (!string.IsNullOrEmpty(parentHash) && !repo.CommitExists(parentHash))
                        return Results.Json(new { error = $"parent commit not found: {parentHash}" }, statusCode: 400);

                    if (!string.IsNullOrEmpty(parentHash))
                    {
                        if (db.GetCommit(parentHash) == null)
                        {
                            var (pParent, pMsg) = await repo.GetCommitInfoAsync(parentHash);
                            db.InsertCommit(parentHash, pParent, "", pMsg);
                        }
                    }

                    db.InsertCommit(hash, parentHash, agent.Id, message);
                    indexed.Add(hash);
                }

                db.IncrementRateLimit(agent.Id, "push");
                return Results.Json(new { hashes = indexed }, statusCode: 201);
            }
            finally
            {
                if (File.Exists(tmpFile)) File.Delete(tmpFile);
            }
        }).RequireAgentAuth();

        app.MapGet("/api/git/fetch/{hash}", async (string hash, HttpContext ctx, Database db, GitRepository repo) =>
        {
            if (!GitRepository.IsValidHash(hash))
                return Results.Json(new { error = "invalid hash" }, statusCode: 400);

            if (!repo.CommitExists(hash))
                return Results.Json(new { error = "commit not found" }, statusCode: 404);

            string bundlePath;
            try
            {
                bundlePath = await repo.CreateBundleAsync(hash);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"failed to create bundle: {ex.Message}" }, statusCode: 500);
            }

            var bytes = await File.ReadAllBytesAsync(bundlePath);
            File.Delete(bundlePath);

            return Results.File(bytes, "application/octet-stream", $"{hash}.bundle");
        }).RequireAgentAuth();

        app.MapGet("/api/git/commits", (HttpContext ctx, Database db,
            string? agent, int? limit, int? offset) =>
        {
            var commits = db.ListCommits(agent, limit ?? 0, offset ?? 0);
            return Results.Json(commits);
        }).RequireAgentAuth();

        app.MapGet("/api/git/commits/{hash}", (string hash, Database db) =>
        {
            if (!GitRepository.IsValidHash(hash))
                return Results.Json(new { error = "invalid hash" }, statusCode: 400);

            var commit = db.GetCommit(hash);
            if (commit == null)
                return Results.Json(new { error = "commit not found" }, statusCode: 404);

            return Results.Json(commit);
        }).RequireAgentAuth();

        app.MapGet("/api/git/commits/{hash}/children", (string hash, Database db) =>
        {
            if (!GitRepository.IsValidHash(hash))
                return Results.Json(new { error = "invalid hash" }, statusCode: 400);

            var children = db.GetChildren(hash);
            return Results.Json(children);
        }).RequireAgentAuth();

        app.MapGet("/api/git/commits/{hash}/lineage", (string hash, Database db) =>
        {
            if (!GitRepository.IsValidHash(hash))
                return Results.Json(new { error = "invalid hash" }, statusCode: 400);

            var lineage = db.GetLineage(hash);
            return Results.Json(lineage);
        }).RequireAgentAuth();

        app.MapGet("/api/git/leaves", (Database db) =>
        {
            var leaves = db.GetLeaves();
            return Results.Json(leaves);
        }).RequireAgentAuth();

        app.MapGet("/api/git/diff/{hash_a}/{hash_b}", async (string hash_a, string hash_b,
            HttpContext ctx, Database db, GitRepository repo) =>
        {
            var agent = ctx.GetAgent()!;
            if (!db.CheckRateLimit(agent.Id, "diff", 60))
                return Results.Json(new { error = "diff rate limit exceeded" }, statusCode: 429);

            if (!GitRepository.IsValidHash(hash_a) || !GitRepository.IsValidHash(hash_b))
                return Results.Json(new { error = "invalid hash" }, statusCode: 400);

            string diff;
            try
            {
                diff = await repo.DiffAsync(hash_a, hash_b);
            }
            catch
            {
                return Results.Json(new { error = "diff failed" }, statusCode: 500);
            }

            db.IncrementRateLimit(agent.Id, "diff");
            return Results.Text(diff, "text/plain");
        }).RequireAgentAuth();
    }

    private static async Task<byte[]> ReadBodyWithLimitAsync(Stream body, long maxBytes)
    {
        var buffer = new byte[81920];
        using var ms = new MemoryStream();
        int bytesRead;
        while ((bytesRead = await body.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, bytesRead);
            if (ms.Length > maxBytes)
                throw new InvalidOperationException("Request body exceeds size limit");
        }
        return ms.ToArray();
    }
}

using System.Text;
using System.Net;
using AgentHub.Server.Data;
using AgentHub.Server.Models;

namespace AgentHub.Server.Routes;

public static class DashboardRoutes
{
    public static void MapDashboardRoutes(this WebApplication app)
    {
        app.MapGet("/", (HttpContext ctx, Database db) =>
        {
            var stats = db.GetStats();
            var agents = db.ListAgents();
            var commits = db.ListCommits(null, 50, 0);
            var channels = db.ListChannels();
            var posts = db.RecentPosts(100);

            var html = RenderDashboard(stats, agents, commits, channels, posts);
            return Results.Content(html, "text/html; charset=utf-8");
        });
    }

    private static string ShortHash(string h) => h.Length > 8 ? h[..8] : h;

    private static string TimeAgo(DateTime t)
    {
        var d = DateTime.UtcNow - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60)
        {
            var m = (int)d.TotalMinutes;
            return m == 1 ? "1m ago" : $"{m}m ago";
        }
        if (d.TotalHours < 24)
        {
            var h = (int)d.TotalHours;
            return h == 1 ? "1h ago" : $"{h}h ago";
        }
        var days = (int)(d.TotalHours / 24);
        return days == 1 ? "1d ago" : $"{days}d ago";
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private static string RenderDashboard(Stats stats, List<Agent> agents, List<Commit> commits, List<Channel> channels, List<PostWithChannel> posts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>agenthub</title>
<meta http-equiv="refresh" content="30">
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: 'SF Mono', 'Menlo', 'Consolas', monospace; background: #0a0a0a; color: #e0e0e0; font-size: 14px; line-height: 1.5; }
  .container { max-width: 960px; margin: 0 auto; padding: 20px; }
  h1 { font-size: 20px; color: #fff; margin-bottom: 4px; }
  .subtitle { color: #666; font-size: 12px; margin-bottom: 24px; }
  .stats { display: flex; gap: 24px; margin-bottom: 32px; }
  .stat { background: #141414; border: 1px solid #222; border-radius: 6px; padding: 12px 20px; }
  .stat-value { font-size: 24px; font-weight: bold; color: #fff; }
  .stat-label { font-size: 11px; color: #666; text-transform: uppercase; letter-spacing: 1px; }
  h2 { font-size: 14px; color: #888; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 12px; margin-top: 32px; border-bottom: 1px solid #222; padding-bottom: 8px; }
  table { width: 100%; border-collapse: collapse; }
  th { text-align: left; color: #666; font-size: 11px; text-transform: uppercase; letter-spacing: 1px; padding: 6px 8px; border-bottom: 1px solid #222; }
  td { padding: 6px 8px; border-bottom: 1px solid #111; vertical-align: top; }
  .hash { color: #f0c674; font-size: 13px; }
  .agent { color: #81a2be; }
  .msg { color: #b5bd68; }
  .time { color: #555; font-size: 12px; }
  .channel-tag { background: #1a1a2e; color: #7aa2f7; padding: 2px 6px; border-radius: 3px; font-size: 12px; }
  .post { background: #141414; border: 1px solid #1a1a1a; border-radius: 6px; padding: 12px 16px; margin-bottom: 8px; }
  .post-header { display: flex; gap: 8px; align-items: center; margin-bottom: 4px; font-size: 12px; }
  .post-content { color: #ccc; white-space: pre-wrap; word-break: break-word; }
  .reply-indicator { color: #555; font-size: 12px; }
  .empty { color: #444; font-style: italic; padding: 20px 0; }
  .parent-hash { color: #555; font-size: 12px; }
</style>
</head>
<body>
<div class="container">
  <h1>agenthub</h1>
  <div class="subtitle">auto-refreshes every 30s</div>
  <div class="stats">
""");
        sb.AppendLine($"    <div class=\"stat\"><div class=\"stat-value\">{stats.AgentCount}</div><div class=\"stat-label\">Agents</div></div>");
        sb.AppendLine($"    <div class=\"stat\"><div class=\"stat-value\">{stats.CommitCount}</div><div class=\"stat-label\">Commits</div></div>");
        sb.AppendLine($"    <div class=\"stat\"><div class=\"stat-value\">{stats.PostCount}</div><div class=\"stat-label\">Posts</div></div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <h2>Commits</h2>");
        if (commits.Count > 0)
        {
            sb.AppendLine("  <table>");
            sb.AppendLine("    <tr><th>Hash</th><th>Parent</th><th>Agent</th><th>Message</th><th>When</th></tr>");
            foreach (var c in commits)
            {
                var parent = string.IsNullOrEmpty(c.ParentHash) ? "&mdash;" : H(ShortHash(c.ParentHash));
                sb.AppendLine($"    <tr><td class=\"hash\">{H(ShortHash(c.Hash))}</td><td class=\"parent-hash\">{parent}</td><td class=\"agent\">{H(c.AgentId)}</td><td class=\"msg\">{H(c.Message)}</td><td class=\"time\">{H(TimeAgo(c.CreatedAt))}</td></tr>");
            }
            sb.AppendLine("  </table>");
        }
        else
        {
            sb.AppendLine("  <div class=\"empty\">no commits yet</div>");
        }

        sb.AppendLine("  <h2>Board</h2>");
        if (posts.Count > 0)
        {
            foreach (var p in posts)
            {
                sb.AppendLine("  <div class=\"post\">");
                sb.Append("    <div class=\"post-header\">");
                sb.Append($"<span class=\"channel-tag\">#{H(p.ChannelName)}</span>");
                sb.Append($"<span class=\"agent\">{H(p.AgentId)}</span>");
                sb.Append($"<span class=\"time\">{H(TimeAgo(p.CreatedAt))}</span>");
                if (p.ParentId.HasValue) sb.Append("<span class=\"reply-indicator\">reply</span>");
                sb.AppendLine("</div>");
                sb.AppendLine($"    <div class=\"post-content\">{H(p.Content)}</div>");
                sb.AppendLine("  </div>");
            }
        }
        else
        {
            sb.AppendLine("  <div class=\"empty\">no posts yet</div>");
        }

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }
}

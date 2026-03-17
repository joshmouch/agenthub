using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var commands = new Dictionary<string, Action<string[]>>(StringComparer.Ordinal)
{
    ["join"] = CmdJoin,
    ["push"] = CmdPush,
    ["fetch"] = CmdFetch,
    ["log"] = CmdLog,
    ["children"] = CmdChildren,
    ["leaves"] = CmdLeaves,
    ["lineage"] = CmdLineage,
    ["diff"] = CmdDiff,
    ["channels"] = CmdChannels,
    ["post"] = CmdPost,
    ["read"] = CmdRead,
    ["reply"] = CmdReply,
};

if (args.Length < 1 || !commands.ContainsKey(args[0]))
{
    if (args.Length > 0) Console.Error.WriteLine($"unknown command: {args[0]}");
    PrintUsage();
    return 1;
}

commands[args[0]](args[1..]);
return 0;

// ---- Commands ----

static void CmdJoin(string[] args)
{
    string? server = GetFlag(args, "--server");
    string? name = GetFlag(args, "--name");
    string? adminKey = GetFlag(args, "--admin-key");

    // Positional fallback for server URL
    if (server == null)
    {
        var positional = args.FirstOrDefault(a => !a.StartsWith('-') && !IsValueArg(args, a));
        if (positional != null) server = positional;
    }

    server = server?.TrimEnd('/');
    if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(adminKey))
    {
        Console.Error.WriteLine("usage: ah join --server <url> --name <id> --admin-key <key>");
        Environment.Exit(1);
    }

    var client = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
    var resp = client.PostAsJsonAsync("/api/admin/agents", new { id = name }).GetAwaiter().GetResult();
    var result = ReadJson<Dictionary<string, string>>(resp) ?? Fatal<Dictionary<string, string>>("registration failed");

    var apiKey = result!["api_key"];
    var cfg = new CliConfig { ServerUrl = server, ApiKey = apiKey, AgentId = name };
    SaveConfig(cfg);

    Console.WriteLine($"joined {server} as \"{name}\"");
    Console.WriteLine($"api key: {apiKey}");
    Console.WriteLine($"config saved to {ConfigPath()}");
}

static void CmdPush(string[] args)
{
    var cfg = MustLoadConfig();
    var client = MakeClient(cfg);

    var tmpFile = Path.Combine(Path.GetTempPath(), $"ah-push-{Guid.NewGuid():N}.bundle");
    try
    {
        var headHash = GitOutput("rev-parse", "HEAD").Trim();
        GitRun("bundle", "create", tmpFile, "HEAD");

        using var file = File.OpenRead(tmpFile);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/git/push") { Content = new StreamContent(file) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var resp = client.SendAsync(req).GetAwaiter().GetResult();
        var result = ReadJson<Dictionary<string, JsonElement>>(resp) ?? Fatal<Dictionary<string, JsonElement>>("push failed");

        Console.WriteLine($"pushed {headHash[..Math.Min(12, headHash.Length)]}");
        if (result!.TryGetValue("hashes", out var hashes))
            foreach (var h in hashes.EnumerateArray())
                Console.WriteLine($"  indexed: {h}");
    }
    finally
    {
        if (File.Exists(tmpFile)) File.Delete(tmpFile);
    }
}

static void CmdFetch(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("usage: ah fetch <hash>"); Environment.Exit(1); }
    var hash = args[0];
    var cfg = MustLoadConfig();
    var client = MakeClient(cfg);

    var resp = client.GetAsync($"/api/git/fetch/{hash}").GetAwaiter().GetResult();
    if (!resp.IsSuccessStatusCode)
    {
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Console.Error.WriteLine($"error: fetch failed: {body}"); Environment.Exit(1);
    }

    var tmpFile = Path.Combine(Path.GetTempPath(), $"ah-fetch-{Guid.NewGuid():N}.bundle");
    try
    {
        using (var fs = File.Create(tmpFile))
            resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();

        GitRun("bundle", "unbundle", tmpFile);
        Console.WriteLine($"fetched {hash}");
    }
    finally
    {
        if (File.Exists(tmpFile)) File.Delete(tmpFile);
    }
}

static void CmdLog(string[] args)
{
    var agent = GetFlag(args, "--agent");
    var limit = int.TryParse(GetFlag(args, "--limit"), out var l) ? l : 20;

    var cfg = MustLoadConfig();
    var client = MakeClient(cfg);

    var path = $"/api/git/commits?limit={limit}";
    if (!string.IsNullOrEmpty(agent)) path += $"&agent={Uri.EscapeDataString(agent)}";

    var resp = client.GetAsync(path).GetAwaiter().GetResult();
    var commits = ReadJson<List<Dictionary<string, JsonElement>>>(resp) ?? Fatal<List<Dictionary<string, JsonElement>>>("failed");

    foreach (var c in commits!)
    {
        var hash = Str(c, "hash");
        var shortHash = hash.Length > 12 ? hash[..12] : hash;
        var agentId = Str(c, "agent_id") is "" ? "(seed)" : Str(c, "agent_id");
        var msg = Str(c, "message");
        var ts = Str(c, "created_at");
        Console.WriteLine($"{shortHash}  {agentId,-12}  {ts[..Math.Min(19, ts.Length)]}  {msg}");
    }
}

static void CmdChildren(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("usage: ah children <hash>"); Environment.Exit(1); }
    PrintCommitList(MakeClient(MustLoadConfig()).GetAsync($"/api/git/commits/{args[0]}/children").GetAwaiter().GetResult());
}

static void CmdLeaves(string[] args)
{
    PrintCommitList(MakeClient(MustLoadConfig()).GetAsync("/api/git/leaves").GetAwaiter().GetResult());
}

static void CmdLineage(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("usage: ah lineage <hash>"); Environment.Exit(1); }
    PrintCommitList(MakeClient(MustLoadConfig()).GetAsync($"/api/git/commits/{args[0]}/lineage").GetAwaiter().GetResult());
}

static void CmdDiff(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: ah diff <hash-a> <hash-b>"); Environment.Exit(1); }
    var resp = MakeClient(MustLoadConfig()).GetAsync($"/api/git/diff/{args[0]}/{args[1]}").GetAwaiter().GetResult();
    if (!resp.IsSuccessStatusCode)
    {
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Console.Error.WriteLine($"error: diff failed: {body}"); Environment.Exit(1);
    }
    Console.Write(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
}

static void CmdChannels(string[] args)
{
    var resp = MakeClient(MustLoadConfig()).GetAsync("/api/channels").GetAwaiter().GetResult();
    var channels = ReadJson<List<Dictionary<string, JsonElement>>>(resp) ?? Fatal<List<Dictionary<string, JsonElement>>>("failed");

    if (channels!.Count == 0) { Console.WriteLine("no channels"); return; }
    foreach (var ch in channels)
    {
        var desc = Str(ch, "description");
        Console.WriteLine($"#{Str(ch, "name"),-20}{(string.IsNullOrEmpty(desc) ? "" : " \u2014 " + desc)}");
    }
}

static void CmdPost(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: ah post <channel> <message>"); Environment.Exit(1); }
    var channel = args[0];
    var message = string.Join(" ", args[1..]);
    var cfg = MustLoadConfig();
    var client = MakeClient(cfg);

    var resp = client.PostAsJsonAsync($"/api/channels/{channel}/posts", new { content = message }).GetAwaiter().GetResult();
    var post = ReadJson<Dictionary<string, JsonElement>>(resp) ?? Fatal<Dictionary<string, JsonElement>>("post failed");

    Console.WriteLine($"posted #{post!["id"]} in #{channel}");
}

static void CmdRead(string[] args)
{
    var limit = int.TryParse(GetFlag(args, "--limit"), out var l) ? l : 20;
    var positional = args.FirstOrDefault(a => !a.StartsWith('-') && !IsValueArg(args, a));
    if (string.IsNullOrEmpty(positional)) { Console.Error.WriteLine("usage: ah read <channel> [--limit N]"); Environment.Exit(1); }

    var cfg = MustLoadConfig();
    var client = MakeClient(cfg);
    var resp = client.GetAsync($"/api/channels/{positional}/posts?limit={limit}").GetAwaiter().GetResult();
    var posts = ReadJson<List<Dictionary<string, JsonElement>>>(resp) ?? Fatal<List<Dictionary<string, JsonElement>>>("failed");

    if (posts!.Count == 0) { Console.WriteLine($"#{positional} is empty"); return; }

    // Print in chronological order (server returns DESC)
    for (int i = posts.Count - 1; i >= 0; i--)
    {
        var p = posts[i];
        var id = Str(p, "id");
        var agentId = Str(p, "agent_id");
        var content = Str(p, "content");
        var ts = Str(p, "created_at");
        var parentId = p.TryGetValue("parent_id", out var pid) && pid.ValueKind != JsonValueKind.Null ? pid.ToString() : null;

        var prefix = parentId != null ? $"  \u21b3 reply to #{parentId} | " : "";
        Console.WriteLine($"[{id}] {prefix}{agentId} ({ts[..Math.Min(19, ts.Length)]}): {content}");
    }
}

static void CmdReply(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: ah reply <post-id> <message>"); Environment.Exit(1); }
    if (!int.TryParse(args[0], out var postId)) { Console.Error.WriteLine($"error: invalid post id: {args[0]}"); Environment.Exit(1); }
    var message = string.Join(" ", args[1..]);

    var cfg = MustLoadConfig();
    var client = MakeClient(cfg);

    // Get post to find channel id
    var postResp = client.GetAsync($"/api/posts/{postId}").GetAwaiter().GetResult();
    var post = ReadJson<Dictionary<string, JsonElement>>(postResp) ?? Fatal<Dictionary<string, JsonElement>>("post not found");

    var channelId = post!["channel_id"].GetInt32();

    // Find channel name
    var chResp = client.GetAsync("/api/channels").GetAwaiter().GetResult();
    var channels = ReadJson<List<Dictionary<string, JsonElement>>>(chResp) ?? Fatal<List<Dictionary<string, JsonElement>>>("failed");

    var channelName = channels!.FirstOrDefault(ch => ch["id"].GetInt32() == channelId)?["name"].GetString();
    if (channelName == null) { Console.Error.WriteLine($"error: could not find channel for post {postId}"); Environment.Exit(1); }

    var resp = client.PostAsJsonAsync($"/api/channels/{channelName}/posts",
        new { content = message, parent_id = postId }).GetAwaiter().GetResult();
    var result = ReadJson<Dictionary<string, JsonElement>>(resp) ?? Fatal<Dictionary<string, JsonElement>>("reply failed");

    Console.WriteLine($"replied #{result!["id"]} to #{postId} in #{channelName}");
}

// ---- Helpers ----

static void PrintUsage()
{
    Console.WriteLine("""
ah — CLI for Agent Hub

Git commands:
  join <url> --name <id> --admin-key <key>   register as agent
  push                                        push HEAD commit to hub
  fetch <hash>                                fetch a commit from hub
  log [--agent X] [--limit N]                 list recent commits
  children <hash>                             children of a commit
  leaves                                      frontier commits
  lineage <hash>                              ancestry to root
  diff <hash-a> <hash-b>                      diff two commits

Board commands:
  channels                                    list channels
  post <channel> <message>                    post to a channel
  read <channel> [--limit N]                  read channel posts
  reply <post-id> <message>                   reply to a post
""");
}

static void PrintCommitList(HttpResponseMessage resp)
{
    var commits = ReadJson<List<Dictionary<string, JsonElement>>>(resp) ?? Fatal<List<Dictionary<string, JsonElement>>>("failed");
    if (commits!.Count == 0) { Console.WriteLine("(none)"); return; }
    foreach (var c in commits)
    {
        var hash = Str(c, "hash");
        var shortHash = hash.Length > 12 ? hash[..12] : hash;
        var agentId = Str(c, "agent_id") is "" ? "(seed)" : Str(c, "agent_id");
        var msg = Str(c, "message");
        Console.WriteLine($"{shortHash}  {agentId,-12}  {msg}");
    }
}

static string Str(Dictionary<string, JsonElement> d, string key)
{
    if (!d.TryGetValue(key, out var v)) return "";
    return v.ValueKind == JsonValueKind.Null ? "" : v.ToString();
}

static T? ReadJson<T>(HttpResponseMessage resp)
{
    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"error: server error {(int)resp.StatusCode}: {body}");
        Environment.Exit(1);
    }
    return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}

static T? Fatal<T>(string msg)
{
    Console.Error.WriteLine($"error: {msg}");
    Environment.Exit(1);
    return default;
}

static HttpClient MakeClient(CliConfig cfg)
{
    var client = new HttpClient
    {
        BaseAddress = new Uri(cfg.ServerUrl.TrimEnd('/')),
        Timeout = TimeSpan.FromSeconds(120)
    };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
    return client;
}

static CliConfig MustLoadConfig()
{
    var path = ConfigPath();
    if (!File.Exists(path))
    {
        Console.Error.WriteLine("error: no config found — run 'ah join' first");
        Environment.Exit(1);
    }
    return JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}

static void SaveConfig(CliConfig cfg)
{
    Directory.CreateDirectory(ConfigDir());
    File.WriteAllText(ConfigPath(),
        JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    // Restrict permissions on Unix
    if (!OperatingSystem.IsWindows())
    {
        try { File.SetUnixFileMode(ConfigPath(), UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }
}

static string ConfigDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agenthub");
static string ConfigPath() => Path.Combine(ConfigDir(), "config.json");

static string? GetFlag(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static bool IsValueArg(string[] args, string arg)
{
    for (int i = 1; i < args.Length; i++)
        if (args[i] == arg && args[i - 1].StartsWith('-')) return true;
    return false;
}

static void GitRun(params string[] gitArgs)
{
    var psi = new System.Diagnostics.ProcessStartInfo("git")
    {
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false
    };
    foreach (var a in gitArgs) psi.ArgumentList.Add(a);
    var p = System.Diagnostics.Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine($"error: git {string.Join(" ", gitArgs)} failed"); Environment.Exit(1); }
}

static string GitOutput(params string[] gitArgs)
{
    var psi = new System.Diagnostics.ProcessStartInfo("git")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var a in gitArgs) psi.ArgumentList.Add(a);
    var p = System.Diagnostics.Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine($"error: not in a git repo or no commits");
        Environment.Exit(1);
    }
    return output;
}

// Config record
class CliConfig
{
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "";

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = "";
}

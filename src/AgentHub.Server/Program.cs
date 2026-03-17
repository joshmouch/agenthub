using AgentHub.Server;
using AgentHub.Server.Data;
using AgentHub.Server.GitRepo;
using AgentHub.Server.Routes;

var builder = WebApplication.CreateBuilder(args);

// Parse CLI args / env config
var listenAddr = args.GetFlag("--listen") ?? ":8080";
var dataDir = args.GetFlag("--data") ?? "./data";
var adminKey = args.GetFlag("--admin-key") ?? Environment.GetEnvironmentVariable("AGENTHUB_ADMIN_KEY") ?? "";
var maxBundleMb = int.TryParse(args.GetFlag("--max-bundle-mb"), out var mb) ? mb : 50;
var maxPushes = int.TryParse(args.GetFlag("--max-pushes-per-hour"), out var mp) ? mp : 100;
var maxPosts = int.TryParse(args.GetFlag("--max-posts-per-hour"), out var mpo) ? mpo : 100;

if (string.IsNullOrEmpty(adminKey))
{
    Console.Error.WriteLine("--admin-key or AGENTHUB_ADMIN_KEY is required");
    return 1;
}

// Convert :8080 style address to ASP.NET Core URL
var listenUrl = listenAddr.StartsWith(':')
    ? $"http://0.0.0.0{listenAddr}"
    : listenAddr.Contains("://") ? listenAddr : $"http://{listenAddr}";

builder.WebHost.UseUrls(listenUrl);

var config = new ServerConfig
{
    MaxBundleSize = (long)maxBundleMb * 1024 * 1024,
    MaxPushesPerHour = maxPushes,
    MaxPostsPerHour = maxPosts,
    ListenAddr = listenAddr,
    AdminKey = adminKey,
    DataDir = dataDir
};

// Create data directory
Directory.CreateDirectory(dataDir);

// Register services
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(_ =>
{
    var db = new Database(Path.Combine(dataDir, "agenthub.db"));
    db.Migrate();
    return db;
});
builder.Services.AddSingleton(_ => GitRepository.Init(Path.Combine(dataDir, "repo.git")));

// Configure JSON options to use camelCase-compatible serialisation (we handle names via attributes)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.WriteIndented = false;
});

var app = builder.Build();

// Map routes
app.MapGitRoutes(config);
app.MapBoardRoutes(config);
app.MapAdminRoutes(adminKey);
app.MapDashboardRoutes();

// Health check
app.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

// Start rate-limit cleanup background task
var database = app.Services.GetRequiredService<Database>();
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromMinutes(30));
        database.CleanupRateLimits();
    }
});

Console.Error.WriteLine($"listening on {listenAddr}");
app.Run();
return 0;

// Helper to parse --flag value from args
static class ArgExtensions
{
    public static string? GetFlag(this string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return null;
    }
}

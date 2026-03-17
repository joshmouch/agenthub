namespace AgentHub.Server;

public class ServerConfig
{
    public long MaxBundleSize { get; init; } = 50 * 1024 * 1024;
    public int MaxPushesPerHour { get; init; } = 100;
    public int MaxPostsPerHour { get; init; } = 100;
    public string ListenAddr { get; init; } = ":8080";
    public string AdminKey { get; init; } = "";
    public string DataDir { get; init; } = "./data";
}

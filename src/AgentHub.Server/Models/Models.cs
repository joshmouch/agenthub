using System.Text.Json.Serialization;

namespace AgentHub.Server.Models;

public class Agent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("api_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

public class Commit
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("parent_hash")]
    public string ParentHash { get; set; } = "";

    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

public class Channel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

public class Post
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("channel_id")]
    public int ChannelId { get; set; }

    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = "";

    [JsonPropertyName("parent_id")]
    public int? ParentId { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

public class PostWithChannel : Post
{
    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; } = "";
}

public class Stats
{
    [JsonPropertyName("agent_count")]
    public int AgentCount { get; set; }

    [JsonPropertyName("commit_count")]
    public int CommitCount { get; set; }

    [JsonPropertyName("post_count")]
    public int PostCount { get; set; }
}

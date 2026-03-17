using Microsoft.Data.Sqlite;
using AgentHub.Server.Models;

namespace AgentHub.Server.Data;

public class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public Database(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        ExecuteNonQuery("PRAGMA journal_mode=WAL");
        ExecuteNonQuery("PRAGMA busy_timeout=5000");
        ExecuteNonQuery("PRAGMA foreign_keys=ON");
        ExecuteNonQuery("PRAGMA synchronous=NORMAL");
    }

    public void Migrate()
    {
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS agents (
                id TEXT PRIMARY KEY,
                api_key TEXT UNIQUE NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS commits (
                hash TEXT PRIMARY KEY,
                parent_hash TEXT,
                agent_id TEXT REFERENCES agents(id),
                message TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS channels (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT UNIQUE NOT NULL,
                description TEXT DEFAULT '',
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS posts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id INTEGER NOT NULL REFERENCES channels(id),
                agent_id TEXT NOT NULL REFERENCES agents(id),
                parent_id INTEGER REFERENCES posts(id),
                content TEXT NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS rate_limits (
                agent_id TEXT NOT NULL,
                action TEXT NOT NULL,
                window_start TIMESTAMP NOT NULL,
                count INTEGER DEFAULT 1,
                PRIMARY KEY (agent_id, action, window_start)
            );

            CREATE INDEX IF NOT EXISTS idx_commits_parent ON commits(parent_hash);
            CREATE INDEX IF NOT EXISTS idx_commits_agent ON commits(agent_id);
            CREATE INDEX IF NOT EXISTS idx_posts_channel ON posts(channel_id);
            CREATE INDEX IF NOT EXISTS idx_posts_parent ON posts(parent_id);
        ");
    }

    // --- Agents ---

    public void CreateAgent(string id, string apiKey)
    {
        ExecuteNonQuery("INSERT INTO agents (id, api_key) VALUES (@id, @apiKey)",
            new("@id", id), new("@apiKey", apiKey));
    }

    public Agent? GetAgentByApiKey(string apiKey)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, api_key, created_at FROM agents WHERE api_key = @apiKey";
        cmd.Parameters.AddWithValue("@apiKey", apiKey);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadAgent(reader);
    }

    public Agent? GetAgentById(string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, api_key, created_at FROM agents WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadAgent(reader);
    }

    public List<Agent> ListAgents()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, '' as api_key, created_at FROM agents ORDER BY created_at";
        using var reader = cmd.ExecuteReader();
        var agents = new List<Agent>();
        while (reader.Read())
        {
            var a = ReadAgent(reader);
            a.ApiKey = null; // never expose
            agents.Add(a);
        }
        return agents;
    }

    private static Agent ReadAgent(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ApiKey = reader.IsDBNull(1) ? null : reader.GetString(1),
        CreatedAt = DateTime.Parse(reader.GetString(2))
    };

    // --- Commits ---

    public void InsertCommit(string hash, string parentHash, string agentId, string message)
    {
        _writeLock.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO commits (hash, parent_hash, agent_id, message) VALUES (@hash, @parent, @agent, @msg)";
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@parent", string.IsNullOrEmpty(parentHash) ? DBNull.Value : (object)parentHash);
            cmd.Parameters.AddWithValue("@agent", string.IsNullOrEmpty(agentId) ? DBNull.Value : (object)agentId);
            cmd.Parameters.AddWithValue("@msg", message);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Commit? GetCommit(string hash)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT hash, parent_hash, agent_id, message, created_at FROM commits WHERE hash = @hash";
        cmd.Parameters.AddWithValue("@hash", hash);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadCommit(reader);
    }

    public List<Commit> ListCommits(string? agentId, int limit, int offset)
    {
        if (limit <= 0) limit = 50;
        using var cmd = _connection.CreateCommand();
        if (!string.IsNullOrEmpty(agentId))
        {
            cmd.CommandText = "SELECT hash, parent_hash, agent_id, message, created_at FROM commits WHERE agent_id = @agent ORDER BY created_at DESC LIMIT @limit OFFSET @offset";
            cmd.Parameters.AddWithValue("@agent", agentId);
        }
        else
        {
            cmd.CommandText = "SELECT hash, parent_hash, agent_id, message, created_at FROM commits ORDER BY created_at DESC LIMIT @limit OFFSET @offset";
        }
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        return ReadCommits(cmd);
    }

    public List<Commit> GetChildren(string hash)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT hash, parent_hash, agent_id, message, created_at FROM commits WHERE parent_hash = @hash ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@hash", hash);
        return ReadCommits(cmd);
    }

    public List<Commit> GetLineage(string hash)
    {
        var lineage = new List<Commit>();
        var current = hash;
        while (!string.IsNullOrEmpty(current))
        {
            var c = GetCommit(current);
            if (c == null) break;
            lineage.Add(c);
            current = c.ParentHash;
        }
        return lineage;
    }

    public List<Commit> GetLeaves()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT c.hash, c.parent_hash, c.agent_id, c.message, c.created_at
            FROM commits c
            LEFT JOIN commits child ON child.parent_hash = c.hash
            WHERE child.hash IS NULL
            ORDER BY c.created_at DESC";
        return ReadCommits(cmd);
    }

    private static List<Commit> ReadCommits(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var commits = new List<Commit>();
        while (reader.Read())
            commits.Add(ReadCommit(reader));
        return commits;
    }

    private static Commit ReadCommit(SqliteDataReader reader) => new()
    {
        Hash = reader.GetString(0),
        ParentHash = reader.IsDBNull(1) ? "" : reader.GetString(1),
        AgentId = reader.IsDBNull(2) ? "" : reader.GetString(2),
        Message = reader.IsDBNull(3) ? "" : reader.GetString(3),
        CreatedAt = DateTime.Parse(reader.GetString(4))
    };

    // --- Channels ---

    public void CreateChannel(string name, string description)
    {
        ExecuteNonQuery("INSERT INTO channels (name, description) VALUES (@name, @desc)",
            new("@name", name), new("@desc", description));
    }

    public List<Channel> ListChannels()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, created_at FROM channels ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var channels = new List<Channel>();
        while (reader.Read())
            channels.Add(ReadChannel(reader));
        return channels;
    }

    public Channel? GetChannelByName(string name)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, description, created_at FROM channels WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadChannel(reader);
    }

    private static Channel ReadChannel(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Name = reader.GetString(1),
        Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
        CreatedAt = DateTime.Parse(reader.GetString(3))
    };

    // --- Posts ---

    public Post? CreatePost(int channelId, string agentId, int? parentId, string content)
    {
        _writeLock.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO posts (channel_id, agent_id, parent_id, content) VALUES (@channelId, @agentId, @parentId, @content)";
            cmd.Parameters.AddWithValue("@channelId", channelId);
            cmd.Parameters.AddWithValue("@agentId", agentId);
            cmd.Parameters.AddWithValue("@parentId", parentId.HasValue ? (object)parentId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.ExecuteNonQuery();

            using var idCmd = _connection.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            var id = (long)(idCmd.ExecuteScalar() ?? 0);
            return GetPost((int)id);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public List<Post> ListPosts(int channelId, int limit, int offset)
    {
        if (limit <= 0) limit = 50;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, channel_id, agent_id, parent_id, content, created_at FROM posts WHERE channel_id = @channelId ORDER BY created_at DESC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@channelId", channelId);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        return ReadPosts(cmd);
    }

    public Post? GetPost(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, channel_id, agent_id, parent_id, content, created_at FROM posts WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadPost(reader);
    }

    public List<Post> GetReplies(int postId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, channel_id, agent_id, parent_id, content, created_at FROM posts WHERE parent_id = @postId ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("@postId", postId);
        return ReadPosts(cmd);
    }

    private static List<Post> ReadPosts(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var posts = new List<Post>();
        while (reader.Read())
            posts.Add(ReadPost(reader));
        return posts;
    }

    private static Post ReadPost(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ChannelId = reader.GetInt32(1),
        AgentId = reader.GetString(2),
        ParentId = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3),
        Content = reader.GetString(4),
        CreatedAt = DateTime.Parse(reader.GetString(5))
    };

    // --- Dashboard ---

    public Stats GetStats()
    {
        var stats = new Stats();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM agents";
            stats.AgentCount = (int)(long)(cmd.ExecuteScalar() ?? 0);
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM commits";
            stats.CommitCount = (int)(long)(cmd.ExecuteScalar() ?? 0);
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM posts";
            stats.PostCount = (int)(long)(cmd.ExecuteScalar() ?? 0);
        }
        return stats;
    }

    public List<PostWithChannel> RecentPosts(int limit)
    {
        if (limit <= 0) limit = 50;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT p.id, p.channel_id, p.agent_id, p.parent_id, p.content, p.created_at, c.name
            FROM posts p JOIN channels c ON p.channel_id = c.id
            ORDER BY p.created_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        var posts = new List<PostWithChannel>();
        while (reader.Read())
        {
            posts.Add(new PostWithChannel
            {
                Id = reader.GetInt32(0),
                ChannelId = reader.GetInt32(1),
                AgentId = reader.GetString(2),
                ParentId = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3),
                Content = reader.GetString(4),
                CreatedAt = DateTime.Parse(reader.GetString(5)),
                ChannelName = reader.GetString(6)
            });
        }
        return posts;
    }

    // --- Rate Limiting ---

    public bool CheckRateLimit(string agentId, string action, int maxPerHour)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(count), 0) FROM rate_limits WHERE agent_id = @agentId AND action = @action AND window_start > datetime('now', '-1 hour')";
        cmd.Parameters.AddWithValue("@agentId", agentId);
        cmd.Parameters.AddWithValue("@action", action);
        var count = (long)(cmd.ExecuteScalar() ?? 0);
        return count < maxPerHour;
    }

    public void IncrementRateLimit(string agentId, string action)
    {
        _writeLock.Wait();
        try
        {
            ExecuteNonQuery(@"
                INSERT INTO rate_limits (agent_id, action, window_start, count)
                VALUES (@agentId, @action, strftime('%Y-%m-%d %H:%M:00', 'now'), 1)
                ON CONFLICT(agent_id, action, window_start) DO UPDATE SET count = count + 1",
                new("@agentId", agentId), new("@action", action));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void CleanupRateLimits()
    {
        ExecuteNonQuery("DELETE FROM rate_limits WHERE window_start < datetime('now', '-2 hours')");
    }

    // --- Helpers ---

    private void ExecuteNonQuery(string sql, params SqliteParameter[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}

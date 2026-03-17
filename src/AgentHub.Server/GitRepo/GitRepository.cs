using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentHub.Server.GitRepo;

public partial class GitRepository
{
    public string Path { get; }
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    [GeneratedRegex(@"^[0-9a-f]{4,64}$")]
    private static partial Regex HashRegex();

    public static bool IsValidHash(string s) => HashRegex().IsMatch(s);

    public GitRepository(string path)
    {
        Path = path;
    }

    public static GitRepository Init(string path)
    {
        if (!File.Exists(System.IO.Path.Combine(path, "HEAD")))
        {
            RunGit(null, 60, "init", "--bare", path);
        }
        return new GitRepository(path);
    }

    public async Task<string[]> UnbundleAsync(string bundlePath)
    {
        await _writeLock.WaitAsync();
        try
        {
            var output = await RunGitAsync("bundle", "list-heads", bundlePath);
            var hashes = ParseHeadHashes(output);
            if (hashes.Length == 0)
                throw new InvalidOperationException("bundle contains no refs");

            await RunGitAsync("bundle", "unbundle", bundlePath);
            return hashes;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string> CreateBundleAsync(string commitHash)
    {
        if (!IsValidHash(commitHash))
            throw new ArgumentException($"invalid hash: {commitHash}");

        var tmpRef = $"refs/tmp/bundle-{commitHash[..8]}";
        await RunGitAsync("update-ref", tmpRef, commitHash);
        try
        {
            var tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arhub-bundle-{Guid.NewGuid():N}.bundle");
            await RunGitAsync("bundle", "create", tmpFile, tmpRef);
            return tmpFile;
        }
        finally
        {
            try { await RunGitAsync("update-ref", "-d", tmpRef); } catch { /* best effort */ }
        }
    }

    public bool CommitExists(string hash)
    {
        if (!IsValidHash(hash)) return false;
        try
        {
            RunGit(Path, 30, "cat-file", "-t", hash);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(string parentHash, string message)> GetCommitInfoAsync(string hash)
    {
        if (!IsValidHash(hash))
            throw new ArgumentException($"invalid hash: {hash}");

        var output = await RunGitAsync("log", "-1", "--format=%P%x00%s", hash);
        output = output.TrimEnd('\n');
        var parts = output.Split('\x00', 2);
        var parentHash = "";
        var message = "";
        if (parts.Length >= 1)
        {
            var parents = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length > 0)
                parentHash = parents[0];
        }
        if (parts.Length >= 2)
            message = parts[1];

        return (parentHash, message);
    }

    public async Task<string> DiffAsync(string hashA, string hashB)
    {
        if (!IsValidHash(hashA) || !IsValidHash(hashB))
            throw new ArgumentException("invalid hash");
        return await RunGitAsync("diff", hashA, hashB);
    }

    private Task<string> RunGitAsync(params string[] args)
    {
        return Task.Run(() => RunGit(Path, 60, args));
    }

    private static string RunGit(string? workDir, int timeoutSeconds, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir ?? ""
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        if (!string.IsNullOrEmpty(workDir))
            process.StartInfo.Environment["GIT_DIR"] = workDir;

        process.Start();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill();
            throw new TimeoutException($"git {string.Join(" ", args)} timed out");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");

        return stdout.ToString();
    }

    private static string[] ParseHeadHashes(string output)
    {
        var hashes = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 1 && IsValidHash(fields[0]))
                hashes.Add(fields[0]);
        }
        return [.. hashes];
    }
}

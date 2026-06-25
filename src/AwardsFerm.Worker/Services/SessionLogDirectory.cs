namespace AwardsFerm.Worker.Services;

public static class SessionLogDirectory
{
    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration["Logging:SessionLogDirectory"];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(FindRepoRoot(), "logs", "sessions")
            : Path.GetFullPath(configured);

        Directory.CreateDirectory(root);
        return root;
    }

    public static string GetLogPath(IConfiguration configuration, string sessionId)
    {
        var fileName = SanitizeFileName(sessionId) + ".log";
        return Path.Combine(Resolve(configuration), fileName);
    }

    private static string SanitizeFileName(string sessionId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = sessionId.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "profiles")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

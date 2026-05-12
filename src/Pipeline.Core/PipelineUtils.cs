using Azure.Identity;

namespace Pipeline.Core;

public static class PipelineUtils
{
    /// <summary>
    /// The app data directory: <c>$XDG_DATA_HOME/pipeline-monitor</c> or
    /// <c>~/.local/share/pipeline-monitor</c> on Linux/macOS,
    /// <c>%LOCALAPPDATA%\pipeline-monitor</c> on Windows.
    /// </summary>
    public static string AppDataDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var baseDir = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
            return Path.Combine(baseDir, "pipeline-monitor");
        }
    }

    /// <summary>
    /// Default path to the monitor SQLite database.
    /// </summary>
    public static string DefaultDatabasePath => Path.Combine(AppDataDirectory, "monitor.db");

    /// <summary>
    /// Resolves the database path from command-line arguments (--database) or falls back to <see cref="DefaultDatabasePath"/>.
    /// </summary>
    public static string GetDatabasePath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--database")
                return args[i + 1];
        }

        return DefaultDatabasePath;
    }

    public static DefaultAzureCredential CreateCredential() =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions()
        {
            TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        });
}

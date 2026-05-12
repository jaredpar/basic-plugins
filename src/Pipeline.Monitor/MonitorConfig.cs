using System.Text.Json;
using System.Text.Json.Serialization;
using Pipeline.Core;

namespace Pipeline.Monitor;

/// <summary>
/// Top-level configuration for the monitor service.
/// </summary>
public sealed record MonitorConfig
{
    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
    };

    public static string AppDataDirectory => PipelineUtils.AppDataDirectory;

    public static string DefaultConfigPath => Path.Combine(AppDataDirectory, "config.json");

    public static string DefaultDatabasePath => PipelineUtils.DefaultDatabasePath;

    [JsonPropertyName("pollIntervalMinutes")]
    public int PollIntervalMinutes { get; init; } = 5;

    [JsonPropertyName("database")]
    public string Database { get; init; } = "monitor.db";

    [JsonPropertyName("pipelines")]
    public List<PipelineConfig> Pipelines { get; init; } = [];

    public static MonitorConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<MonitorConfig>(json, s_readOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize config from {path}");

        // Resolve relative database path against the config file's directory
        if (!Path.IsPathRooted(config.Database))
        {
            var configDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            config = config with { Database = Path.Combine(configDir, config.Database) };
        }

        return config;
    }

    /// <summary>
    /// Creates the app data directory and writes a default config file.
    /// Returns the path to the created config file.
    /// </summary>
    public static string Initialize()
    {
        var dir = AppDataDirectory;
        Directory.CreateDirectory(dir);

        var configPath = DefaultConfigPath;
        if (File.Exists(configPath))
        {
            throw new InvalidOperationException(
                $"Config already exists at {configPath}. Delete it first to reinitialize.");
        }

        var defaultConfig = new MonitorConfig
        {
            PollIntervalMinutes = 5,
            Database = "monitor.db",
            Pipelines =
            [
                new PipelineConfig
                {
                    Repository = "dotnet/roslyn",
                    BuildFilter = "all",
                    Enabled = true,
                },
            ],
        };

        var json = JsonSerializer.Serialize(defaultConfig, s_writeOptions);
        File.WriteAllText(configPath, json);
        return configPath;
    }
}

/// <summary>
/// Configuration for a single pipeline to monitor.
/// </summary>
public sealed class PipelineConfig
{
    [JsonPropertyName("repository")]
    public required string Repository { get; init; }

    [JsonPropertyName("definitionIds")]
    public List<int?> DefinitionIds { get; init; } = [null];

    [JsonPropertyName("buildFilter")]
    public string BuildFilter { get; init; } = "all";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

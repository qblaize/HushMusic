using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Services;

public sealed class AppPaths : IAppPaths
{
    /// <summary>The HUSHMUSIC_DATA_ROOT environment variable overrides the folder (isolated test instances).</summary>
    public AppPaths()
        : this(Environment.GetEnvironmentVariable("HUSHMUSIC_DATA_ROOT") is { Length: > 0 } root ? root : DefaultRoot())
    {
    }

    private static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HushMusic");

    public AppPaths(string root)
    {
        Root = Ensure(root);
    }

    public string Root { get; }

    public string Logs => Ensure(Path.Combine(Root, "logs"));

    public string Cache => Ensure(Path.Combine(Root, "cache"));

    public string ImageCache => Ensure(Path.Combine(Root, "cache", "images"));

    public string Secure => Ensure(Path.Combine(Root, "secure"));

    public string Tools => Ensure(Path.Combine(Root, "tools"));

    public string SettingsFile => Path.Combine(Root, "settings.json");

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

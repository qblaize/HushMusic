using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Tests.Parsing;

/// <summary>Loads the JSON fixtures copied next to the test assembly.</summary>
internal static class ParserFixtures
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>A real response captured anonymously (Fixtures/*.json, expectations in Fixtures/EXPECTED.md).</summary>
    public static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, name)))!;

    /// <summary>A hand-assembled or signed-out response under Fixtures/Synthetic.</summary>
    public static JsonNode Synthetic(string name) => Load(Path.Combine("Synthetic", name));

    public static IEnumerable<string> RealFixtureNames() =>
        Directory.GetFiles(Root, "*.json").Select(Path.GetFileName).OfType<string>().Order();

    public static IEnumerable<string> SyntheticFixtureNames(string prefix) =>
        Directory.GetFiles(Path.Combine(Root, "Synthetic"), prefix + "*.json").Select(Path.GetFileName).OfType<string>().Order();

    /// <summary>Navigates a fixture for test mutations; throws when the path is wrong (that is a test bug).</summary>
    public static JsonNode At(this JsonNode node, params object[] path)
    {
        var current = node;
        foreach (var step in path)
        {
            current = step switch
            {
                string key => current[key],
                int index => current[index],
                _ => null,
            } ?? throw new InvalidOperationException($"Fixture path step '{step}' not found.");
        }

        return current;
    }

    /// <summary>Removes a property from the object at <paramref name="path"/>.</summary>
    public static void Delete(this JsonNode node, string property, params object[] path)
    {
        var target = node.At(path).AsObject();
        if (!target.Remove(property))
        {
            throw new InvalidOperationException($"Fixture property '{property}' not found.");
        }
    }

    public static TimeSpan TotalDuration(this IEnumerable<Track> tracks) =>
        tracks.Aggregate(TimeSpan.Zero, (sum, t) => sum + (t.Duration ?? TimeSpan.Zero));
}

/// <summary>Records every log entry so tests can assert that skipped items were reported.</summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> Warnings => Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

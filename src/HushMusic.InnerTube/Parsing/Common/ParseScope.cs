using Microsoft.Extensions.Logging;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>Logger plus the page being parsed, handed to the shared item parsers.</summary>
internal sealed class ParseScope(ILogger logger, string page)
{
    public ILogger Logger { get; } = logger;

    public string Page { get; } = page;

    /// <summary>An item had an unexpected shape and was dropped.</summary>
    public void SkipItem(string renderer, string reason) => ParserLog.SkippedItem(Logger, Page, renderer, reason);

    /// <summary>An item was dropped on purpose (known but unsupported kind, deleted entry...).</summary>
    public void IgnoreItem(string renderer, string reason) => ParserLog.IgnoredItem(Logger, Page, renderer, reason);

    /// <summary>A page-level structure is missing; the parser returns an empty or partial model.</summary>
    public void MissingStructure(string what) => ParserLog.MissingStructure(Logger, Page, what);
}

internal static partial class ParserLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Page}: skipped {Renderer} item: {Reason}")]
    public static partial void SkippedItem(ILogger logger, string page, string renderer, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Page}: ignored {Renderer} item: {Reason}")]
    public static partial void IgnoredItem(ILogger logger, string page, string renderer, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Page}: expected structure is missing: {What}")]
    public static partial void MissingStructure(ILogger logger, string page, string what);
}

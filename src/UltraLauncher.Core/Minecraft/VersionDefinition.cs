namespace UltraLauncher.Core.Minecraft;

/// <summary>Ссылка на файл из метаданных Mojang (url + sha1 + size).</summary>
public sealed record FileRef(string Url, string? Sha1, long Size, string? Path = null);

public sealed record AssetIndexRef(string Id, string Url, string? Sha1, long Size, long TotalSize);

public sealed record JavaRequirement(string Component, int MajorVersion);

public sealed record LoggingConfig(string Argument, string FileId, FileRef File);

/// <summary>Аргумент из блока arguments.jvm/game: значения + условия.</summary>
public sealed record ArgumentEntry(IReadOnlyList<string> Values, IReadOnlyList<Rule>? Rules);

public sealed record Library(
    string Name,
    FileRef? Artifact,
    IReadOnlyDictionary<string, FileRef> Classifiers,
    IReadOnlyDictionary<string, string>? Natives,
    IReadOnlyList<Rule>? Rules,
    IReadOnlyList<string> ExtractExclude,
    string? RepositoryUrl);

/// <summary>Метаданные версии (versions/&lt;id&gt;/&lt;id&gt;.json) после разбора.</summary>
public sealed class VersionDefinition
{
    public required string Id { get; init; }
    public string? InheritsFrom { get; init; }
    public string Type { get; init; } = "release";
    public string? MainClass { get; init; }
    public string? AssetsId { get; init; }
    public AssetIndexRef? AssetIndex { get; init; }
    public FileRef? ClientDownload { get; init; }
    /// <summary>Id версии, jar которой используется (для наследников).</summary>
    public string? JarVersionId { get; init; }
    public IReadOnlyList<Library> Libraries { get; init; } = [];
    public IReadOnlyList<ArgumentEntry> JvmArguments { get; init; } = [];
    public IReadOnlyList<ArgumentEntry> GameArguments { get; init; } = [];
    /// <summary>Старый формат (до 1.13): строка minecraftArguments.</summary>
    public string? LegacyMinecraftArguments { get; init; }
    public JavaRequirement? Java { get; init; }
    public LoggingConfig? Logging { get; init; }
    public DateTimeOffset ReleaseTime { get; init; }

    public bool UsesModernArguments => GameArguments.Count > 0 || JvmArguments.Count > 0;
}

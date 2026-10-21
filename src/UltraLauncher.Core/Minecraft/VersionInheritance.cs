using UltraLauncher.Core.Common;

namespace UltraLauncher.Core.Minecraft;

/// <summary>
/// Объединяет версию-наследника (inheritsFrom) с родителем так же, как официальный лаунчер:
/// библиотеки наследника идут первыми, аргументы дописываются к родительским,
/// скалярные поля наследника переопределяют родительские.
/// </summary>
public static class VersionInheritance
{
    public const int MaxDepth = 8;

    public static VersionDefinition Resolve(VersionDefinition version, Func<string, VersionDefinition> loadParent)
    {
        var current = version;
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { version.Id };
        while (current.InheritsFrom is { } parentId)
        {
            if (++depth > MaxDepth || !seen.Add(parentId))
                throw new LauncherException(ErrorCode.Integrity, $"Цикл или слишком глубокое наследование версий ({parentId}).");
            current = Merge(current, loadParent(parentId));
        }
        return current;
    }

    public static VersionDefinition Merge(VersionDefinition child, VersionDefinition parent)
    {
        var libs = new List<Library>(child.Libraries);
        var childNames = new HashSet<string>(child.Libraries.Select(LibraryKey), StringComparer.Ordinal);
        libs.AddRange(parent.Libraries.Where(l => !childNames.Contains(LibraryKey(l))));

        return new VersionDefinition
        {
            Id = child.Id,
            InheritsFrom = parent.InheritsFrom,
            Type = child.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            AssetsId = child.AssetsId ?? parent.AssetsId,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            ClientDownload = child.ClientDownload ?? parent.ClientDownload,
            JarVersionId = child.ClientDownload is not null ? child.JarVersionId ?? child.Id : child.JarVersionId ?? parent.JarVersionId ?? parent.Id,
            Libraries = libs,
            JvmArguments = [.. parent.JvmArguments, .. child.JvmArguments],
            GameArguments = [.. parent.GameArguments, .. child.GameArguments],
            LegacyMinecraftArguments = child.LegacyMinecraftArguments ?? parent.LegacyMinecraftArguments,
            Java = child.Java ?? parent.Java,
            Logging = child.Logging ?? parent.Logging,
            ReleaseTime = child.ReleaseTime == default ? parent.ReleaseTime : child.ReleaseTime,
        };
    }

    /// <summary>group:artifact[:classifier] без версии — чтобы наследник мог заменить версию библиотеки.</summary>
    private static string LibraryKey(Library lib)
    {
        var parts = lib.Name.Split(':');
        return parts.Length >= 4 ? $"{parts[0]}:{parts[1]}:{parts[3]}" : parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : lib.Name;
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.App.ViewModels;

public sealed class VersionOption
{
    public required VersionItem Item { get; init; }
    public string Id => Item.Id;
    public bool IsInstalled => Item.InstallState == InstallState.Complete;
    public bool IsIncomplete => Item.InstallState == InstallState.Incomplete;
    public bool IsSnapshot => Item.Type == "snapshot";

    public string TypeText => Item.Type switch
    {
        "release" => "релиз",
        "snapshot" => "снапшот",
        "old_beta" => "beta",
        "old_alpha" => "alpha",
        _ => Item.Type,
    };

    public string StateText => IsInstalled ? "установлена" : IsIncomplete ? "установка не завершена" : "не установлена";

    public string DateText => Item.ReleaseTime == default ? "" : Item.ReleaseTime.ToLocalTime().ToString("dd.MM.yyyy");

    public string Display => $"{Id}  ·  {TypeText}" + (Item.IsLatest ? "  ·  последняя" : "");

    public override string ToString() => Id;
}

/// <summary>Список версий из официального манифеста, общий для главной и загрузок.</summary>
public sealed partial class VersionCatalog : ObservableObject
{
    private readonly GameService _game;
    private readonly NotificationViewModel _notifications;
    private List<VersionOption> _all = [];

    public VersionCatalog(GameService game, NotificationViewModel notifications)
    {
        _game = game;
        _notifications = notifications;
    }

    /// <summary>Поддерживаемые версии с учётом настройки «показывать снапшоты».</summary>
    public ObservableCollection<VersionOption> Visible { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _sourceNotice;
    [ObservableProperty] private string? _latestRelease;
    [ObservableProperty] private bool _showSnapshots;

    public event Action? Reloaded;

    public VersionOption? Find(string? id) => id is null ? null : _all.FirstOrDefault(v => v.Id == id);

    partial void OnShowSnapshotsChanged(bool value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var result = await Task.Run(() => _game.GetVersionsAsync(CancellationToken.None));
            _all = result.Versions.Select(v => new VersionOption { Item = v }).ToList();
            LatestRelease = result.LatestRelease;
            SourceNotice = !result.FromCache ? null
                : result.CachedAtUtc is { } at
                    ? $"Нет связи с сервером Mojang — показан список версий от {at.ToLocalTime():dd.MM.yyyy HH:mm}."
                    : "Нет связи с сервером Mojang — показаны только установленные версии.";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ex);
            SourceNotice = "Список версий недоступен. Проверьте подключение к интернету.";
        }
        finally
        {
            IsLoading = false;
            Reloaded?.Invoke();
        }
    }

    /// <summary>Обновляет только отметки «установлена» без запроса к сети.</summary>
    public void RefreshInstallStates()
    {
        var installed = _game.SafeListInstalled().ToDictionary(v => v.Id, v => v.State, StringComparer.OrdinalIgnoreCase);
        _all = _all.Select(o => new VersionOption
        {
            Item = o.Item with { InstallState = installed.TryGetValue(o.Id, out var s) ? s : null },
        }).ToList();
        foreach (var local in installed.Where(i => _all.All(a => a.Id != i.Key)))
            _all.Add(new VersionOption { Item = new VersionItem(local.Key, "release", default, true, null, local.Value, false) });
        ApplyFilter();
        Reloaded?.Invoke();
    }

    private void ApplyFilter()
    {
        Visible.Clear();
        // Неподдерживаемые версии (до 1.8, alpha/beta) не показываются, чтобы не выдавать их за рабочие.
        foreach (var v in _all.Where(v => v.Item.IsSupported && (ShowSnapshots || !v.IsSnapshot || v.IsInstalled)))
            Visible.Add(v);
    }
}

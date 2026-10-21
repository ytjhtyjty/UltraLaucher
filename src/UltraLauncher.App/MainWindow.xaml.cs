using System.Windows;

namespace UltraLauncher.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => FitToWorkArea();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CurrentKey)) SyncNav(vm.CurrentKey); };
        };
    }

    /// <summary>Переход по выбору пункта меню — мышью, клавиатурой (стрелки) или средствами доступности.</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { CommandParameter: string key } && DataContext is ViewModels.MainViewModel vm && vm.CurrentKey != key)
            vm.Navigate(key);
    }

    /// <summary>Отмечает пункт меню при программной навигации (например, кнопка «Аккаунты»).</summary>
    private void SyncNav(string key)
    {
        foreach (var rb in NavPanel.Children.OfType<System.Windows.Controls.RadioButton>())
            if (rb.CommandParameter as string == key && rb.IsChecked != true) rb.IsChecked = true;
    }

    /// <summary>
    /// При масштабе 125–150% на небольших экранах окно по умолчанию может не поместиться —
    /// уменьшаем его до рабочей области (с учётом панели задач).
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (Width > area.Width * 0.95) Width = Math.Max(MinWidth, area.Width * 0.95);
        if (Height > area.Height * 0.95) Height = Math.Max(Math.Min(MinHeight, area.Height), area.Height * 0.95);
        if (MinHeight > area.Height) MinHeight = area.Height;
        if (MinWidth > area.Width) MinWidth = area.Width;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel { Operation.IsActive: true } vm)
        {
            var answer = MessageBox.Show(this,
                $"Сейчас выполняется: {vm.Operation.Title}.\nЗакрыть UltraLauncher и прервать операцию? Уже скачанные файлы сохранятся.",
                "UltraLauncher", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
            vm.Operation.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}

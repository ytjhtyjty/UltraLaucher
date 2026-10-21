using System.Collections.Specialized;
using System.Windows.Controls;

namespace UltraLauncher.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        AutoScroll(LauncherLogList);
        AutoScroll(GameLogList);
    }

    /// <summary>Прокручивает журнал к последней строке, если пользователь не листает его вверх.</summary>
    private static void AutoScroll(ListBox list)
    {
        if (list.Items is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += (_, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Add || list.Items.Count == 0) return;
                var scroller = FindScroller(list);
                if (scroller is null || scroller.VerticalOffset >= scroller.ScrollableHeight - 2)
                    list.ScrollIntoView(list.Items[^1]);
            };
        }
        list.Loaded += (_, _) => { if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1]); };
    }

    private static ScrollViewer? FindScroller(System.Windows.DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var r = FindScroller(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            if (r is not null) return r;
        }
        return null;
    }
}

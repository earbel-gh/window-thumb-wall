using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowThumbWall.Tests;

public sealed class SidebarScrollTests
{
    [Fact]
    public void SpecificWindowListShowsItsVerticalScrollbarOnlyOnOverflow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                var layout = (FrameworkElement)window.Content;
                var list = (ListBox)window.FindName("SpecificWindowsList");
                list.DisplayMemberPath = string.Empty;
                foreach (int count in new[] { 2, 22 })
                {
                    list.ItemsSource = Enumerable.Range(1, count)
                        .Select(index => $"Window {index}: {new string('W', 120)}")
                        .ToArray();
                    layout.Measure(new Size(1200, 780));
                    layout.Arrange(new Rect(0, 0, 1200, 780));
                    layout.UpdateLayout();
                    ScrollViewer viewer = Assert.IsType<ScrollViewer>(FindViewer(list));
                    Assert.Equal(count == 22 ? Visibility.Visible : Visibility.Collapsed,
                        viewer.ComputedVerticalScrollBarVisibility);
                    Assert.Equal(count == 22, viewer.ScrollableHeight > 0);
                    var bar = Assert.IsType<System.Windows.Controls.Primitives.ScrollBar>(
                        viewer.Template.FindName("PART_VerticalScrollBar", viewer));
                    Assert.Equal(count == 22 ? Visibility.Visible : Visibility.Collapsed, bar.Visibility);
                    if (count == 22)
                    {
                        Assert.True(bar.ActualWidth > 0);
                        Assert.True(bar.ActualHeight > 0);
                        Point origin = bar.TranslatePoint(new Point(), list);
                        Assert.InRange(origin.X + bar.ActualWidth, 1, list.ActualWidth + 0.01);
                        Assert.InRange(origin.Y + bar.ActualHeight, 1, list.ActualHeight + 0.01);
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static ScrollViewer? FindViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer)
            return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindViewer(VisualTreeHelper.GetChild(parent, i)) is { } child)
                return child;
        return null;
    }
}

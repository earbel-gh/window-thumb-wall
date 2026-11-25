using System.Windows;
using System.Windows.Controls;

namespace WindowThumbWall.Tests;

public sealed class SettingsWindowTests
{
    [Fact]
    public void Constructor_ReflectsInitialNotificationSetting()
    {
        RunInSta(() =>
        {
            SettingsWindow window = new(
                osNotificationAttentionEnabled: true,
                notificationAvailabilityText: "unavailable in this run",
                _ => { });

            CheckBox checkBox = FindLogicalDescendant<CheckBox>(window.Content);

            Assert.Equal(LocalizedText.Get("label.settings"), window.Title);
            Assert.Equal(LocalizedText.Get("setting.osNotifications"), checkBox.Content);
            Assert.True(checkBox.IsChecked);
            Assert.True(checkBox.IsEnabled);
            Grid root = Assert.IsType<Grid>(window.Content);
            Assert.Contains(root.Children.OfType<TextBlock>(), text =>
                text.Text.Contains("unavailable in this run", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void CheckboxChange_NotifiesCallbackWithUpdatedValue()
    {
        RunInSta(() =>
        {
            List<bool> observed = [];
            SettingsWindow window = new(
                osNotificationAttentionEnabled: false,
                notificationAvailabilityText: "subscribed",
                enabled => observed.Add(enabled));

            CheckBox checkBox = FindLogicalDescendant<CheckBox>(window.Content);

            checkBox.IsChecked = true;
            checkBox.IsChecked = false;

            Assert.Equal([true, false], observed);
        });
    }

    [Fact]
    public void NotificationStatusGrowth_KeepsInitialWidthAndAllowsContentHeightGrowth()
    {
        RunInSta(() =>
        {
            SettingsWindow window = new(
                osNotificationAttentionEnabled: true,
                notificationAvailabilityText: "ready",
                _ => { });
            const double initialWidth = 420;
            const double initialHeight = 260;

            Assert.Equal(SizeToContent.WidthAndHeight, window.SizeToContent);
            window.KeepInitialWidthAndGrowHeight(initialWidth, initialHeight);

            Assert.Equal(SizeToContent.Height, window.SizeToContent);
            Assert.Equal(initialWidth, window.Width);
            Assert.Equal(initialWidth, window.MinWidth);
            Assert.Equal(initialHeight, window.MinHeight);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);

            Grid root = Assert.IsType<Grid>(window.Content);
            TextBlock status = Assert.Single(root.Children.OfType<TextBlock>(), text =>
                text.Text.Contains("ready", StringComparison.Ordinal));
            Assert.Equal(TextWrapping.Wrap, status.TextWrapping);
            Size available = new(initialWidth, double.PositiveInfinity);
            root.Measure(available);
            root.Arrange(new Rect(root.DesiredSize));
            double shortContentHeight = root.DesiredSize.Height;

            window.UpdateNotificationAvailability(
                string.Join(" ", Enumerable.Repeat("notification listener status details", 12)));
            root.InvalidateMeasure();
            root.Measure(available);

            Assert.True(root.DesiredSize.Height > shortContentHeight,
                $"Expected wrapped notification status to grow the content from {shortContentHeight} to more than {root.DesiredSize.Height}.");
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? captured = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured != null)
            throw new Xunit.Sdk.XunitException($"STA test failed: {captured}");
    }

    private static T FindLogicalDescendant<T>(object? root) where T : DependencyObject
    {
        if (root is T typed)
            return typed;

        if (root is not DependencyObject dependencyObject)
            throw new InvalidOperationException($"Could not find child of type {typeof(T).Name}.");

        foreach (object? child in LogicalTreeHelper.GetChildren(dependencyObject))
        {
            if (child is T directMatch)
                return directMatch;

            try
            {
                return FindLogicalDescendant<T>(child);
            }
            catch (InvalidOperationException)
            {
                // Continue searching siblings.
            }
        }

        throw new InvalidOperationException($"Could not find child of type {typeof(T).Name}.");
    }
}

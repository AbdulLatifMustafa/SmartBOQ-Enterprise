using System;
using System.Windows;
using System.Windows.Controls;

namespace SmartBOQ.App.Views;

/// <summary>
/// Professional vertical Sidebar Navigation component for SmartBOQ Enterprise.
/// Provides ergonomic, eye-comfort side navigation with full responsive behavior.
/// </summary>
public partial class AppSidebar : UserControl
{
    private bool _isUpdating;

    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(
            nameof(SelectedIndex),
            typeof(int),
            typeof(AppSidebar),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public AppSidebar()
    {
        InitializeComponent();
        Loaded += AppSidebar_Loaded;
    }

    private void AppSidebar_Loaded(object sender, RoutedEventArgs e)
    {
        SyncVisualState(SelectedIndex);
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppSidebar control)
        {
            control.SyncVisualState((int)e.NewValue);
        }
    }

    private void SyncVisualState(int index)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        try
        {
            switch (index)
            {
                case 0: if (TabRadio0 != null) TabRadio0.IsChecked = true; break;
                case 1: if (TabRadio1 != null) TabRadio1.IsChecked = true; break;
                case 2: if (TabRadio2 != null) TabRadio2.IsChecked = true; break;
                case 3: if (TabRadio3 != null) TabRadio3.IsChecked = true; break;
                case 4: if (TabRadio4 != null) TabRadio4.IsChecked = true; break;
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void TabRadio_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        if (sender is FrameworkElement elem && int.TryParse(elem.Tag?.ToString(), out int targetIndex))
        {
            SelectedIndex = targetIndex;
        }
    }
}

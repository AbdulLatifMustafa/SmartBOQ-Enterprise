using System;
using System.Windows;
using System.Windows.Controls;

namespace SmartBOQ.App.Views;

/// <summary>
/// Dedicated interactive SliderBar Navigation component for SmartBOQ Enterprise.
/// Hosts smooth horizontal sliding navigation tabs, sliding indicator, and quick controls.
/// </summary>
public partial class SliderNavBar : UserControl
{
    private bool _isUpdating;

    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(
            nameof(SelectedIndex),
            typeof(int),
            typeof(SliderNavBar),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public SliderNavBar()
    {
        InitializeComponent();
        Loaded += SliderNavBar_Loaded;
    }

    private void SliderNavBar_Loaded(object sender, RoutedEventArgs e)
    {
        SyncVisualState(SelectedIndex);
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SliderNavBar control)
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
            if (NavSlider != null && Math.Abs(NavSlider.Value - index) > 0.01)
            {
                NavSlider.Value = index;
            }

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

    private void NavSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating) return;
        int targetIndex = (int)Math.Round(e.NewValue);
        if (targetIndex >= 0 && targetIndex <= 4 && targetIndex != SelectedIndex)
        {
            SelectedIndex = targetIndex;
        }
    }

    private void SlideLeftBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex < 4)
        {
            SelectedIndex++;
        }
        else if (SliderTabsScrollViewer != null)
        {
            SliderTabsScrollViewer.LineRight();
        }
    }

    private void SlideRightBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex > 0)
        {
            SelectedIndex--;
        }
        else if (SliderTabsScrollViewer != null)
        {
            SliderTabsScrollViewer.LineLeft();
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LittleLauncher.Models;

namespace LittleLauncher.Windows;

public sealed class FlyoutItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is LauncherItem { IsCategory: true })
            return CategoryTemplate!;
        return ItemTemplate!;
    }
}

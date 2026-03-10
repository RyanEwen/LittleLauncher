using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LittleLauncher.Models;

namespace LittleLauncher.Windows;

public sealed class FlyoutItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }
    public DataTemplate? HeadingTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is LauncherItem { IsGroup: true })
            return GroupTemplate!;
        if (item is LauncherItem { IsHeading: true })
            return HeadingTemplate!;
        return ItemTemplate!;
    }
}

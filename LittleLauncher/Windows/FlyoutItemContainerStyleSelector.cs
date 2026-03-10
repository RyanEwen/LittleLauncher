using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LittleLauncher.Models;

namespace LittleLauncher.Windows;

public sealed class FlyoutItemContainerStyleSelector : StyleSelector
{
    public Style? GroupStyle { get; set; }
    public Style? HeadingStyle { get; set; }
    public Style? ItemStyle { get; set; }

    protected override Style SelectStyleCore(object item, DependencyObject container)
    {
        if (item is LauncherItem { IsGroup: true })
            return GroupStyle!;
        if (item is LauncherItem { IsHeading: true })
            return HeadingStyle!;
        return ItemStyle!;
    }
}

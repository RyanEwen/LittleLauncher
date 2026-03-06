using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SelfHostedHelper.Models;

namespace SelfHostedHelper.Windows;

public sealed class FlyoutItemContainerStyleSelector : StyleSelector
{
    public Style? CategoryStyle { get; set; }
    public Style? ItemStyle { get; set; }

    protected override Style SelectStyleCore(object item, DependencyObject container)
    {
        if (item is LauncherItem { IsCategory: true })
            return CategoryStyle!;
        return ItemStyle!;
    }
}

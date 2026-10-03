using System.Windows;

namespace CampusAP.App.Services;

/// <summary>主题切换：替换 Application 资源中的主题字典，全部 DynamicResource 引用即时生效。</summary>
public static class ThemeManager
{
    public static void Apply(bool dark)
    {
        var md = System.Windows.Application.Current.Resources.MergedDictionaries;
        var src = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        for (var i = md.Count - 1; i >= 0; i--)
            if (md[i].Source?.OriginalString.Contains("Themes/") == true)
                md.RemoveAt(i);
        md.Insert(0, new ResourceDictionary { Source = src });
    }
}

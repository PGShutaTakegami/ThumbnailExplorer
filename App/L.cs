using System;
using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace ThumbnailExplorer;

[MarkupExtensionReturnType(typeof(string))]
public sealed class L(string key) : MarkupExtension
{
    private static readonly ResourceManager Strings = new("ThumbnailExplorer.Strings", typeof(L).Assembly);

    public override object ProvideValue(IServiceProvider serviceProvider) => Get(key);

    internal static string Get(string name) =>
        Strings.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new InvalidOperationException($"Missing UI string: {name}");

    internal static string Format(string name, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), arguments);
}

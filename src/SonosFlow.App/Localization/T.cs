using System.Windows.Data;
using System.Windows.Markup;

namespace SonosFlow.App.Localization;

/// <summary>
/// XAML: <c>Text="{loc:T main.stream.start}"</c>. Produces a binding to <see cref="Loc"/>'s indexer, so the text
/// follows language changes live. Works on any dependency property, including Run.Text, ToolTip and
/// AutomationProperties.Name.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class T(string key) : MarkupExtension
{
    public T() : this("") { }

    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

namespace TrayFolder.ViewModels;

// The group header uses {Binding}, because an x:Bind group header crashes the XAML compiler, so the
// type must be [Bindable] to keep working when trimmed. The attribute is WinUI's and ViewModels/ also
// compiles in the plain net10.0 test project, so it goes on this part, which only the app builds.
[Microsoft.UI.Xaml.Data.Bindable]
public sealed partial class FileGroupViewModel;

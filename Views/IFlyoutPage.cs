namespace TrayFolder.Views;

/// <summary>A page the flyout hosts. It works only between <see cref="OnShown"/> and <see cref="OnHidden"/>.</summary>
internal interface IFlyoutPage : IDisposable
{
    void OnShown();

    void OnHidden();
}

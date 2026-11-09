using TrayFolder.Core;

namespace TrayFolder.Tests;

[TestClass]
public sealed class HotkeyGestureTests
{
    [TestMethod]
    public void Default_IsCtrlAltF()
    {
        Assert.AreEqual("Ctrl+Alt+F", HotkeyGesture.Default.ToString());
        Assert.IsTrue(HotkeyGesture.Default.IsValid);
    }

    [TestMethod]
    [DataRow("Ctrl+Alt+F", HotkeyModifiers.Control | HotkeyModifiers.Alt, (uint)'F')]
    [DataRow("ctrl + shift + 7", HotkeyModifiers.Control | HotkeyModifiers.Shift, (uint)'7')]
    [DataRow("Win+Alt+F12", HotkeyModifiers.Windows | HotkeyModifiers.Alt, 0x7Bu)]
    [DataRow("Control+Space", HotkeyModifiers.Control, 0x20u)]
    public void TryParse_ValidText_ReturnsGesture(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.AreEqual(new HotkeyGesture(modifiers, key), HotkeyGesture.TryParse(text));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("F")]
    [DataRow("Ctrl+Alt")]
    [DataRow("Ctrl+A+B")]
    [DataRow("Ctrl+F25")]
    [DataRow("Ctrl+Tab")]
    public void TryParse_InvalidText_ReturnsNull(string? text)
    {
        Assert.IsNull(HotkeyGesture.TryParse(text));
    }

    [TestMethod]
    public void ToString_RoundTripsThroughTryParse()
    {
        HotkeyGesture gesture = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, 0x70);

        Assert.AreEqual("Ctrl+Alt+Shift+Win+F1", gesture.ToString());
        Assert.AreEqual(gesture, HotkeyGesture.TryParse(gesture.ToString()));
    }

    [TestMethod]
    public void IsValid_NoModifierOrUnsupportedKey_IsFalse()
    {
        Assert.IsFalse(new HotkeyGesture(HotkeyModifiers.None, 'F').IsValid);
        Assert.IsFalse(new HotkeyGesture(HotkeyModifiers.Control, 0x09).IsValid);
    }
}

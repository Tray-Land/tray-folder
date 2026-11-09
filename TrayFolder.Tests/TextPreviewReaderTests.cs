using System.Text;
using TrayFolder.Core;

namespace TrayFolder.Tests;

[TestClass]
public sealed class TextPreviewReaderTests
{
    [TestMethod]
    public async Task Read_Utf16Bom_DecodesTextRatherThanTreatingNulAsBinary()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "hello", Encoding.Unicode);
            TextPreview preview = await TextPreviewReader.ReadAsync(path, CancellationToken.None);
            Assert.AreEqual("hello", preview.Text);
            Assert.IsFalse(preview.Truncated);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Read_BinaryWithNul_ReturnsUnpreviewable()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 0, 3]);
            TextPreview preview = await TextPreviewReader.ReadAsync(path, CancellationToken.None);
            Assert.IsNull(preview.Text);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Read_LargeText_BoundsPreviewAndReportsTruncation()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new string('x', FileKinds.MaxTextPreviewBytes + 100));
            TextPreview preview = await TextPreviewReader.ReadAsync(path, CancellationToken.None);
            Assert.AreEqual(FileKinds.MaxTextPreviewBytes, preview.Text!.Length);
            Assert.IsTrue(preview.Truncated);
        }
        finally { File.Delete(path); }
    }
}

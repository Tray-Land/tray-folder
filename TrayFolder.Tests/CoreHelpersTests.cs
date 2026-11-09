using TrayFolder.Core;

namespace TrayFolder.Tests;

[TestClass]
public sealed class LatestRequestTests
{
    [TestMethod]
    public void Begin_SecondRequest_CancelsTheFirst()
    {
        LatestRequest latest = new();

        using LatestRequest.Scope first = latest.Begin(CancellationToken.None);
        using LatestRequest.Scope second = latest.Begin(CancellationToken.None);

        Assert.IsTrue(first.Token.IsCancellationRequested);
        Assert.IsFalse(second.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void Begin_LifetimeEnds_CancelsTheRequest()
    {
        using CancellationTokenSource lifetime = new();
        LatestRequest latest = new();
        using LatestRequest.Scope request = latest.Begin(lifetime.Token);

        lifetime.Cancel();

        Assert.IsTrue(request.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void Cancel_AfterRequestFinished_IsHarmless()
    {
        LatestRequest latest = new();
        LatestRequest.Scope request = latest.Begin(CancellationToken.None);
        request.Dispose();

        latest.Cancel();

        Assert.IsFalse(request.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void Dispose_OlderScopeAfterNewerBegan_LeavesTheNewerCancelable()
    {
        LatestRequest latest = new();
        LatestRequest.Scope older = latest.Begin(CancellationToken.None);
        using LatestRequest.Scope newer = latest.Begin(CancellationToken.None);

        older.Dispose();
        latest.Cancel();

        Assert.IsTrue(newer.Token.IsCancellationRequested);
    }
}

[TestClass]
public sealed class FolderPathsTests
{
    [TestMethod]
    [DataRow(@"C:\Files", @"c:\files")]
    [DataRow(@"C:\Files\", @"C:\Files")]
    [DataRow(@"C:\", @"c:\")]
    public void Same_DifferentSpellings_AreTheSameFolder(string a, string b) => Assert.IsTrue(FolderPaths.Same(a, b));

    [TestMethod]
    public void Same_DifferentFolders_AreNot() => Assert.IsFalse(FolderPaths.Same(@"C:\Files", @"C:\Files2"));

    [TestMethod]
    [DataRow(@"C:\Users\me\Downloads", "Downloads")]
    [DataRow(@"C:\Users\me\Downloads\", "Downloads")]
    [DataRow(@"D:\", @"D:\")]
    public void LeafName_ReturnsTheFoldersOwnName(string path, string expected) => Assert.AreEqual(expected, FolderPaths.LeafName(path));
}

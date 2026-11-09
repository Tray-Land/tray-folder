using System.Diagnostics;
using TrayFolder.Core;

namespace TrayFolder.Services;

internal sealed class ErrorReporter : IErrorReporter
{
    public void Report(string operation, Exception exception) => Debug.WriteLine($"{operation} failed: {exception}");
}

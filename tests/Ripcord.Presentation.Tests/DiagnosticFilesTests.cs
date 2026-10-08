using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

public class DiagnosticFilesTests
{
    // Placeholder profile: the point is that whatever the user name is, it does not reach the screen.
    private const string LocalAppData = @"C:\Users\<user>\AppData\Local";

    [Fact]
    public void APathUnderLocalAppData_IsShownWithoutTheUserName()
    {
        string shown = DiagnosticFiles.ForScreen(
            @"C:\Users\<user>\AppData\Local\Ripcord\state\session-trace-20261008-061726.csv", LocalAppData);

        Assert.Equal(@"%LOCALAPPDATA%\Ripcord\state\session-trace-20261008-061726.csv", shown);
        Assert.DoesNotContain("<user>", shown);
    }

    [Fact]
    public void TheMatchIgnoresCase_AsWindowsPathsDo()
        => Assert.StartsWith(
            "%LOCALAPPDATA%",
            DiagnosticFiles.ForScreen(@"c:\users\<USER>\appdata\local\Ripcord\x.csv", LocalAppData));

    [Theory]
    [InlineData(@"D:\Elsewhere\trace.csv")]
    [InlineData(@"C:\Users\<user>\AppData\LocalLow\trace.csv")]
    public void APathElsewhere_IsShownAsItIs(string path)
        => Assert.Equal(path, DiagnosticFiles.ForScreen(path, LocalAppData));

    [Fact]
    public void Pruning_KeepsTheNewestByWhenTheyWereWritten()
    {
        DateTime t0 = new(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
        (string, DateTime)[] traces =
        [
            ("b.csv", t0.AddMinutes(2)),
            ("a.csv", t0),
            ("d.csv", t0.AddMinutes(4)),
            ("c.csv", t0.AddMinutes(3)),
        ];

        Assert.Equal(["c.csv", "b.csv", "a.csv"], DiagnosticFiles.ToPrune(traces, keep: 1));
        Assert.Empty(DiagnosticFiles.ToPrune(traces, keep: 4));
    }
}

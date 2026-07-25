using PalPanel.Control;

public class LogReaderTests
{
    [Fact]
    public void Tail_ReturnsLastNLines_OldestFirst()
    {
        var content = "a\nb\nc\nd\ne\n";
        var tail = LogReader.Tail(content, 3);
        Assert.Equal(new[] { "c", "d", "e" }, tail);
    }

    [Fact]
    public void Tail_FewerLinesThanMax_ReturnsAll()
    {
        Assert.Equal(new[] { "one", "two" }, LogReader.Tail("one\ntwo", 10));
    }

    [Fact]
    public void Tail_NormalizesCrlf_AndTrimsTrailingBlank()
    {
        var tail = LogReader.Tail("x\r\ny\r\n", 5);
        Assert.Equal(new[] { "x", "y" }, tail);
    }

    [Fact]
    public void Tail_EmptyOrZeroMax_IsEmpty()
    {
        Assert.Empty(LogReader.Tail("", 5));
        Assert.Empty(LogReader.Tail("a\nb", 0));
    }

    [Fact]
    public async Task ReadTailAsync_MissingFile_ReturnsNotExistsWithPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "palpanel-no-such-" + Guid.NewGuid() + ".log");
        var view = await LogReader.ReadTailAsync(path, 100);
        Assert.False(view.Exists);
        Assert.Equal(path, view.Path);
        Assert.Empty(view.Lines);
    }

    [Fact]
    public async Task ReadTailAsync_ReadsTail_WhileFileIsOpenForWrite()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}")));
            // Hold the file open for writing to prove the shared read handle works.
            await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var view = await LogReader.ReadTailAsync(path, 5);
            Assert.True(view.Exists);
            Assert.Equal(new[] { "line 496", "line 497", "line 498", "line 499", "line 500" }, view.Lines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PalLogPath_IsUnderSavedLogs()
    {
        var p = LogReader.PalLogPath(@"D:\x\Pal\Saved");
        Assert.Equal("Pal.log", Path.GetFileName(p));
        Assert.Equal("Logs", Path.GetFileName(Path.GetDirectoryName(p)));
    }

    [Theory]
    [InlineData("-port=8211 -log -players=32", true)]
    [InlineData("-log", true)]
    [InlineData("-port=8211 -players=32", false)]
    [InlineData("-logcmds", false)]
    [InlineData("-nolog", false)]
    [InlineData("", false)]
    public void HasLogArg_WordBoundaryMatched(string args, bool expected) =>
        Assert.Equal(expected, LogReader.HasLogArg(args));
}

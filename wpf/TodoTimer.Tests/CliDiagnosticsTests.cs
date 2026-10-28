using System.IO;
using TodoTimer.Services;
using Xunit;

namespace TodoTimer.Tests;

/// <summary>
/// CLI 诊断（2026-10-28 用户报「cli list 返回空」）：数据文件不存在 ≠ 空库——
/// repo 里的 publish/Debug 副本各读各的 data，静默空列表没法诊断，必须在 stderr 提示。
/// </summary>
public sealed class CliDiagnosticsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("todotimer-cli").FullName;
    private readonly TextWriter _origErr = Console.Error;

    [Fact]
    public void DataFileMissing_Stats_PrintsHintToStderr()
    {
        var err = new StringWriter();
        try
        {
            Console.SetError(err);
            var code = CliRunner.Run(new[] { "stats" }, _dir);   // 空目录：无 todos.json
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetError(_origErr);
        }
        Assert.Contains("数据文件不存在", err.ToString());
        Assert.Contains("待办计时器\\TodoTimer.exe", err.ToString());
    }

    [Fact]
    public void DataFileMissing_List_AlsoHints_AndEmptyBody()
    {
        var err = new StringWriter();
        var @out = new StringWriter();
        var origErr = Console.Error;
        var origOut = Console.Out;
        try
        {
            Console.SetError(err);
            Console.SetOut(@out);
            var code = CliRunner.Run(new[] { "list" }, _dir);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetError(origErr);
            Console.SetOut(origOut);
        }
        Assert.Contains("数据文件不存在", err.ToString());
        Assert.Equal(string.Empty, @out.ToString());   // 正文仍为空（无数据），提示走 stderr
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
    }
}

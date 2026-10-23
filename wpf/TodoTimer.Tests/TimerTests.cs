using System.IO;
using TodoTimer.Models;
using TodoTimer.ViewModels;

namespace TodoTimer.Tests;

/// <summary>计时语义测试 —— 对照 Python TodoWidget.start/pause/set_complete 与装载补算。</summary>
public sealed class TimerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("todotimer-timer").FullName;

    private static int Now => (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private MainViewModel NewVm(params TodoItem[] seed)
    {
        TodoStore.Save(Path.Combine(_dir, "todos.json"), new TodoFile
        { Todos = new List<TodoItem>(seed) });
        var vm = new MainViewModel(_dir);
        vm.LoadFile();
        return vm;
    }

    [Fact]
    public void PauseTimer_BakesLiveElapsed()
    {
        var vm = NewVm(new TodoItem
        { Id = "t", Text = "任务", Elapsed = 30, Running = true, LastStartedAt = Now - 60 });
        vm.PauseTimer("t");
        var row = vm.Rows.First(r => !r.IsHeader && r.Todo.Id == "t");
        Assert.False(row.Todo.Running);
        Assert.Null(row.Todo.LastStartedAt);
        Assert.InRange(row.Todo.Elapsed, 89, 91);   // 30 + 60（±1s 容差）
    }

    [Fact]
    public void ToggleComplete_MovesToToday_AndStopsClock()
    {
        var vm = NewVm(new TodoItem
        {
            Id = "t", Text = "跨日任务", Date = "2026-10-01",
            Elapsed = 10, Running = true, LastStartedAt = Now - 5,
        });
        _vmToggle(vm, "t");
        var row = vm.Rows.First(r => !r.IsHeader && r.Todo.Id == "t");
        Assert.True(row.Todo.Completed);
        Assert.False(row.Todo.Running);
        Assert.Equal(TodoItem.TodayStr(), row.Todo.Date);   // 完成归到当天
        Assert.True(row.Todo.Elapsed >= 15);                // 停表固化
    }

    [Fact]
    public void TogglePanelVisible_HidesAndPauses()
    {
        var vm = NewVm(new TodoItem
        { Id = "t", Text = "任务", Visible = true, Elapsed = 0, Running = true, LastStartedAt = Now - 30 });
        vm.TogglePanelVisible("t");
        var row = vm.Rows.First(r => !r.IsHeader && r.Todo.Id == "t");
        Assert.False(row.Todo.Visible);
        Assert.False(row.Todo.Running);
        Assert.InRange(row.Todo.Elapsed, 29, 31);
    }

    [Fact]
    public void Load_CatchesUpOfflineTime()
    {
        TodoStore.Save(Path.Combine(_dir, "todos.json"), new TodoFile
        {
            Todos = new List<TodoItem>
            {
                new() { Id = "t", Text = "任务", Elapsed = 50, Running = true, LastStartedAt = Now - 100 },
            },
        });
        var vm = new MainViewModel(_dir);
        vm.LoadFile();
        var row = vm.Rows.First(r => !r.IsHeader && r.Todo.Id == "t");
        Assert.True(row.Todo.Running);                       // 运行态跨重启保持
        Assert.InRange(row.Todo.Elapsed, 149, 152);          // 50 + 100（±2s 容差）
    }

    private static void _vmToggle(MainViewModel vm, string id)
    {
        vm.GetType();   // 保持与 VM 命令一致的入口
        vm.ToggleCompleteCommand.Execute(id);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

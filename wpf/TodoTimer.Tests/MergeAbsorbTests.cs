using System.IO;
using System.Text.Json;
using TodoTimer.Models;
using TodoTimer.ViewModels;
using Xunit;
using Remote = TodoTimer.Models.RemoteItem;

namespace TodoTimer.Tests;

/// <summary>
/// 云同步合并落地测试（2026-10-28 用户报：手机新建任务电脑端无法加入计时面板，
/// 重启才恢复）——根因=合并只 Rebuild 不重建 _byId 索引；远端新增条目全部操作静默失效。
/// </summary>
public sealed class MergeAbsorbTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("todotimer-merge").FullName;

    private MainViewModel NewVm(params TodoItem[] seed)
    {
        TodoStore.Save(Path.Combine(_dir, "todos.json"), new TodoFile
        { Todos = new List<TodoItem>(seed) });
        var vm = new MainViewModel(_dir);
        vm.LoadFile();
        return vm;
    }

    private static Remote RemoteOf(TodoItem t, long ua)
    {
        t.UpdatedAt = ua;
        return new Remote(t.Id, ua, null, JsonSerializer.SerializeToElement(t));
    }

    [Fact]
    public void RemoteAddedTask_PanelToggle_WorksAfterAbsorb()
    {
        var vm = NewVm(new TodoItem { Id = "1", Text = "本地任务" });

        // 手机端新建 id=170 的任务（不经 AttachSync，直接模拟合并落表；visible=false 待加入面板）
        var remote = new TodoItem { Id = "170", Text = "手机建的", Visible = false };
        Assert.True(SyncOps.ApplyToList(vm.AllTodos, RemoteOf(remote, 9999)));

        vm.AbsorbRemoteChanges();                          // 合并回调里的吸收步骤
        vm.TogglePanelVisible("170");                      // 此前这里静默 return

        Assert.True(vm.AllTodos.Single(t => t.Id == "170").Visible);
    }

    [Fact]
    public void RemoteAddedTask_HighWaterAbsorbed_NoIdCollision()
    {
        var vm = NewVm(new TodoItem { Id = "1", Text = "本地任务" });

        var remote = new TodoItem { Id = "170", Text = "手机建的" };
        SyncOps.ApplyToList(vm.AllTodos, RemoteOf(remote, 9999));
        vm.AbsorbRemoteChanges();

        vm.AddTodo("桌面新建", null, out _);               // 不吸收高水位会再生成 id=170
        Assert.NotEqual("170", vm.AllTodos.First(t => t.Text == "桌面新建").Id);
    }

    [Fact]
    public void RemoteDeletedTask_PanelToggle_NoThrow()
    {
        var vm = NewVm(new TodoItem { Id = "1", Text = "任务", Visible = false });
        vm.TogglePanelVisible("404");                      // 不存在的 id：静默，不抛
        Assert.False(vm.AllTodos.Single(t => t.Id == "1").Visible);   // 本地任务不受影响
    }

    [Theory]
    [InlineData("1.4.1", "1.4.0", true)]    // 服务器新 → 提示
    [InlineData("1.4.0", "1.4.1", false)]   // 服务器旧 → 不提示（本地已超前）
    [InlineData("1.4.0", "1.4.0", false)]   // 相同 → 不提示
    [InlineData("1.5", "1.4.9", true)]      // 短版本号语义化比较
    public void UpdateHint_OnlyWhenServerNewer(string server, string local, bool expected)
        => Assert.Equal(expected, Services.SyncService.IsNewerVersion(server, local));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
    }
}

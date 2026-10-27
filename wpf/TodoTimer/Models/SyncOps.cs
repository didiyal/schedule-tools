using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TodoTimer.Models;

/// <summary>服务端下行的单条变更（/api/changes 响应里的 items 元素）。</summary>
public sealed record RemoteItem(string Id, long UpdatedAt, long? DeletedAt, JsonElement Json);

public enum MergeDecision { AddRemote, TakeRemote, KeepLocal, DeleteLocal }

/// <summary>
/// 同步纯逻辑：指纹 / 变更检测 / LWW 合并决策。无 IO，GUI 与单测共用。
/// LWW 语义：按 UpdatedAt（秒）后写者胜；删除走墓碑（DeletedAt）防复活。
/// </summary>
public static class SyncOps
{
    /// <summary>业务内容指纹（不含 UpdatedAt 自身）——变更检测的唯一依据。</summary>
    public static string Fingerprint(TodoItem t)
    {
        var s = string.Join('\x1f',
            t.Text, t.Parent ?? "", t.Elapsed, t.Completed ? 1 : 0, t.X, t.Y,
            t.Visible ? 1 : 0, t.Order, t.Starred ? 1 : 0, t.Running ? 1 : 0,
            t.LastStartedAt?.ToString(CultureInfo.InvariantCulture) ?? "",
            t.TargetSeconds?.ToString(CultureInfo.InvariantCulture) ?? "",
            t.Reminded ? 1 : 0, t.Date, t.ArchivedAt ?? "", t.RolledAt ?? "", t.RolledFrom ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }

    /// <summary>
    /// 把相对指纹表有变化/新增的条目 UpdatedAt 统一置为 now 并刷新指纹表；
    /// 返回指纹表中已消失的 id（调用方记墓碑）。必须幂等：同内容重复调用不再改时间戳。
    /// </summary>
    public static List<string> BumpChanged(List<TodoItem> todos, Dictionary<string, string> fps, long now)
    {
        foreach (var t in todos)
        {
            var fp = Fingerprint(t);
            if (!fps.TryGetValue(t.Id, out var old) || old != fp)
            {
                t.UpdatedAt = now;
                fps[t.Id] = fp;
            }
        }
        var removed = fps.Keys.Where(id => !todos.Any(t => t.Id == id)).ToList();
        foreach (var id in removed) fps.Remove(id);
        return removed;
    }

    public static MergeDecision Decide(TodoItem? local, RemoteItem remote)
        => remote.DeletedAt is not null ? MergeDecision.DeleteLocal
         : local is null ? MergeDecision.AddRemote
         : remote.UpdatedAt > local.UpdatedAt ? MergeDecision.TakeRemote
         : MergeDecision.KeepLocal;

    /// <summary>把一条远端变更应用进本地列表。返回是否产生了变化（应触发重建+存盘）。</summary>
    public static bool ApplyToList(List<TodoItem> todos, RemoteItem remote)
    {
        var local = todos.FirstOrDefault(t => t.Id == remote.Id);
        switch (Decide(local, remote))
        {
            case MergeDecision.DeleteLocal:
                if (local is null) return false;
                todos.Remove(local);
                return true;
            case MergeDecision.AddRemote:
            case MergeDecision.TakeRemote:
                var item = FromRemoteJson(remote.Json);
                if (item is null) return false;   // 坏 JSON 丢弃，不污染本地
                item.Id = remote.Id;
                item.UpdatedAt = remote.UpdatedAt;
                if (local is null) todos.Add(item);
                else todos[todos.IndexOf(local)] = item;
                return true;
            default:
                return false;
        }
    }

    public static TodoItem? FromRemoteJson(JsonElement json)
    {
        try { return json.Deserialize<TodoItem>(); }
        catch { return null; }
    }
}

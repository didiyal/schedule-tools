using System.Security.Cryptography;
using System.Text;

namespace TodoTimer.Models;

/// <summary>
/// 任务领域操作——GUI（MainViewModel）、CLI（CliRunner）、顺延（DayRollover）
/// 共用的唯一实现点。此前完成/停表/结转链在三个调用方各写一份，语义漂移风险高
/// （v1.5 重构收敛于此）。全部静态：就地改 TodoItem，不碰 UI/存储/时钟（now 由调用方注入）。
/// </summary>
public static class TaskOps
{
    /// <summary>停表并把实时增量固化进 elapsed（幂等：未运行则不动）。</summary>
    public static void StopTimer(TodoItem t, long now)
    {
        if (t.Running && t.LastStartedAt is int ls)
            t.Elapsed += (int)Math.Max(0, now - ls);
        t.Running = false;
        t.LastStartedAt = null;
    }

    /// <summary>
    /// 完成——补卡语义（2026-10-27 用户拍板）：日期保持原所属，不强制改期到今天；
    /// 昨天忘勾的任务今天补勾，历史留在昨天。返回是否变更（原本已完成则 false）。
    /// </summary>
    public static bool Complete(TodoItem t, long now)
    {
        if (t.Completed) return false;
        StopTimer(t, now);
        t.Completed = true;
        return true;
    }

    /// <summary>取消完成——仅翻标记；不逆翻结转链（历史"后来做完了"保持绿）。返回是否变更。</summary>
    public static bool Uncomplete(TodoItem t)
    {
        if (!t.Completed) return false;
        t.Completed = false;
        return true;
    }

    /// <summary>
    /// 结转链翻篇：完成今日承接根时，沿 rolled_from 链把旧日结转根同步置完成
    /// （历史快照跟随翻篇；链上已完成的跳过但继续走，支持多级顺延）。seen 防环。
    /// </summary>
    public static void CompleteRolledChain(TodoItem t, IReadOnlyDictionary<string, TodoItem> byId, long now)
    {
        var seen = new HashSet<string>();
        var cur = t.RolledFrom;
        while (cur is not null && seen.Add(cur) && byId.TryGetValue(cur, out var old))
        {
            Complete(old, now);
            cur = old.RolledFrom;
        }
    }

    /// <summary>时长格式化（数字式 h:mm:ss / m:ss）——计时面板与 CLI 列表共用。</summary>
    public static string FormatDuration(int seconds)
    {
        int h = seconds / 3600;
        int m = seconds % 3600 / 60;
        int s = seconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
    }
}

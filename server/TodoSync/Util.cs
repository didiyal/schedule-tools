using System.Security.Cryptography;
using System.Text;

namespace TodoSync;

/// <summary>通用工具：令牌/指纹/常数时间比较/时间戳。</summary>
public static class Util
{
    /// <summary>"tds_" 前缀 + 32 字节随机数 base64url。</summary>
    public static string NewToken()
    {
        var b = RandomNumberGenerator.GetBytes(32);
        return "tds_" + Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>常数时间字符串比较（令牌/配对码校验用，防时序侧信道）。</summary>
    public static bool FixedEq(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

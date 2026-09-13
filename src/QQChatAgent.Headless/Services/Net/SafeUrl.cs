using System.Net;
using System.Net.Sockets;

namespace QQChatAgent.Services.Net;

/// <summary>
/// 出站 URL 的安全闸门（SSRF 防护）：图片下载与链接预览共用同一套判断。
///
/// 为什么必须拦：这些 URL 来自群消息，任何人都能发。
/// 不拦的话群友发一个 http://169.254.169.254/latest/meta-data/（云元数据）
/// 或 http://napcat:3001/（容器内服务）就能让服务器替他去读内网。
/// </summary>
public static class SafeUrl
{
    /// <summary>校验一个出站 URL；不通过时返回 false 并给出中文原因。</summary>
    public static bool TryValidate(string? url, bool allowPrivate, out Uri uri, out string reason)
    {
        uri = null!;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            reason = "不是 http/https 地址";
            return false;
        }

        if (allowPrivate)
        {
            uri = parsed;
            return true;
        }

        var host = parsed.DnsSafeHost.Trim('[', ']');
        if (host.Length == 0)
        {
            reason = "没有主机名";
            return false;
        }

        // 单标签主机名（localhost / napcat / redis …）一律拒绝：真实站点必定带点
        if (!host.Contains('.'))
        {
            reason = "单标签主机名（疑似内网服务）";
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            reason = "内网域名";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
                ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            {
                reason = "回环/未指定地址";
                return false;
            }

            if (IsPrivateV4(ip) || (ip.IsIPv4MappedToIPv6 && IsPrivateV4(ip.MapToIPv4())))
            {
                reason = "私有地址";
                return false;
            }
        }

        uri = parsed;
        return true;
    }

    private static bool IsPrivateV4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10                                 // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)               // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)               // 169.254.0.0/16 链路本地（云元数据）
            || b[0] == 127                                // 127.0.0.0/8
            || b[0] == 0;                                 // 0.0.0.0/8
    }
}

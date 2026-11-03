using System.Collections.Immutable;
namespace Aict.Core;

// Domains include subdomains. Page rules ignore query/fragment; globs match the complete
// HTTP(S) URL (without fragment), case-insensitively. No user-provided regular expressions.
public static class WebsitePattern {
    public static bool IsDomain(string value)=>!value.Contains('*') && value.IndexOfAny(['/',':','?','#'])<0;
    public static string Normalize(string value) {
        value=value.Trim();
        if(value.Length is 0 or >2048 || value.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)) || value.Contains('\\'))throw new ArgumentException("网站规则须为域名、HTTP(S) 页面网址或使用 * 的通配符，每行一条，最长2048字符。");
        if(value=="file://*.*")return value; // Harmless imported exception; never matches HTTP(S).
        if(IsDomain(value))return PolicyValidation.Domain(value);
        if(value.Contains('*')) {
            if(value.Contains("://") && !value.StartsWith("http://",StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://",StringComparison.OrdinalIgnoreCase) && !value.StartsWith("*://",StringComparison.Ordinal))throw new ArgumentException("通配符仅支持 HTTP(S) 网站；本地文件不受网站保护。");
            if(value.Contains('@'))throw new ArgumentException("网站规则不能包含登录凭据。");
            return value.ToLowerInvariant();
        }
        if(!Uri.TryCreate(value.Contains("://")?value:"https://"+value,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length!=0)throw new ArgumentException("网址格式无效：请填写域名或 HTTP(S) 页面网址。");
        return uri.IdnHost.ToLowerInvariant()+(uri.IsDefaultPort?"":":"+uri.Port)+uri.AbsolutePath.TrimEnd('/');
    }
    public static string WebUrl(string value) {
        if(value.Length>8192 || !Uri.TryCreate(value,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length!=0)throw new ArgumentException("浏览器网址无效。");
        return uri.GetComponents(UriComponents.SchemeAndServer|UriComponents.PathAndQuery,UriFormat.UriEscaped);
    }
    public static bool Glob(string pattern,string value) {
        int p=0,v=0,star=-1,retry=0;
        while(v<value.Length){if(p<pattern.Length && char.ToLowerInvariant(pattern[p])==char.ToLowerInvariant(value[v]) && pattern[p]!='*'){p++;v++;}else if(p<pattern.Length && pattern[p]=='*'){star=p++;retry=v;}else if(star>=0){p=star+1;v=++retry;}else return false;}
        while(p<pattern.Length && pattern[p]=='*')p++;return p==pattern.Length;
    }
    public static bool Matches(string pattern,string urlOrDomain) {
        pattern=Normalize(pattern);if(pattern=="file://*.*")return false;
        var uri=new Uri(WebUrl(urlOrDomain.Contains("://")?urlOrDomain:"https://"+urlOrDomain+"/"));
        string host=uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if(IsDomain(pattern))return host==pattern || host.EndsWith('.'+pattern,StringComparison.Ordinal);
        string full=WebUrl(uri.AbsoluteUri);
        if(pattern.Contains('*'))return Glob(pattern.Contains("://")?pattern:"*://"+pattern,full);
        return string.Equals(pattern,host+(uri.IsDefaultPort?"":":"+uri.Port)+uri.AbsolutePath.TrimEnd('/'),StringComparison.OrdinalIgnoreCase);
    }
    public static bool In(ImmutableArray<string> patterns,string url)=>patterns.Any(p=>Matches(p,url));
}

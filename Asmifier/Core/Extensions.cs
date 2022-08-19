using System.Text.RegularExpressions;
using AsmResolver;

namespace Asmifier.Core; 

public static class Extensions {
    
    public static string FormatBool(this bool value) => value.ToString().ToLower();
    
    public static string Quote(this string? s) => (s ?? "").CreateEscapedString();
    public static string Quote(this Utf8String? s) => Quote((string?)s);

    public static string AsUsableString(this string? s) => Regex.Replace(s ?? "", "[^a-zA-Z0-9]", string.Empty);
    public static string AsUsableString(this Utf8String? s) => AsUsableString((string?)s);
}
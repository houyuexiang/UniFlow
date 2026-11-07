namespace UniFlow.Tests.Common;

/// <summary>
/// 源码文本断言辅助：把注释剥掉，只留可执行代码。
/// <para>
/// 用途：负向断言（"代码里不得再出现 X"）必须针对**代码**而不是注释——
/// 注释里可以写"旧实现曾用 File.ReadAllText(...)"这类历史说明，
/// 若不剥注释，断言会误报。
/// </para>
/// </summary>
public static class SourceText
{
    /// <summary>剥掉行注释（//）与块注释（/* */），字符串/字符字面量内的内容保留。</summary>
    public static string StripComments(string code)
    {
        var lines = code.Split('\n');
        var kept = new List<string>();
        var inBlockComment = false;
        foreach (var raw in lines)
        {
            var line = raw;
            if (inBlockComment)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) continue;
                inBlockComment = false;
                line = line[(end + 2)..];
            }
            var sb = new System.Text.StringBuilder();
            var inString = false;
            var inChar = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\') { if (i + 1 < line.Length) sb.Append(line[++i]); continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    sb.Append(c);
                    if (c == '\\') { if (i + 1 < line.Length) sb.Append(line[++i]); continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); continue; }
                if (c == '\'') { inChar = true; sb.Append(c); continue; }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;   // 行注释到此为止
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlockComment = true; i++; continue; }
                sb.Append(c);
            }
            kept.Add(sb.ToString());
        }
        return string.Join('\n', kept);
    }

    /// <summary>读文件并剥掉注释（只留代码）。</summary>
    public static string CodeOf(string path) => StripComments(File.ReadAllText(path));

    /// <summary>从测试输出目录向上找仓库根，再拼相对路径取文件。</summary>
    public static string RepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo file not found: " + Path.Combine(relativeParts));
    }
}

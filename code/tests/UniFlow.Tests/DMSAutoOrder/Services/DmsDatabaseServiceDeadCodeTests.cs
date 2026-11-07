using Shouldly;

namespace UniFlow.Tests.DMSAutoOrder.Services;

/// <summary>
/// M-01：<c>GetUnknownPatientOrdersAsync</c> 死方法删除的守护用例。
/// <para>
/// 历史缺陷：该方法的 SQL 写成 <c>WHERE (A) AND (B) OR (C)</c>（无括号）→
/// 实际语义是 <c>(A AND B) OR C</c>，<c>codoid NOT IN (...) </c> 单独成为 OR 分支 →
/// 会把所有"无订单"的 reqtube 全纳入。且全仓 0 调用方（旧 DMS 自动下单遗留）。
/// 现在：方法已删除 → 本用例防止它被误加回来（未修的等价物必须先修 SQL 优先级）。
/// </para>
/// </summary>
public class DmsDatabaseServiceDeadCodeTests
{
    [Fact]
    public void GetUnknownPatientOrdersAsync_IsNotExposed()
    {
        var t = typeof(UniFlow.DMSAutoOrder.Services.DmsDatabaseService);
        t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
            .Any(m => m.Name == "GetUnknownPatientOrdersAsync")
            .ShouldBeFalse("M-01 已删除该死方法（0 调用方 + SQL 优先级错误）；若要恢复必须先修 SQL 并补测试");
    }

    [Fact]
    public void GetUnknownPatientOrdersAsync_HasNoCallersInRepo()
    {
        // 全仓扫描（排除 bin/obj/archives 与本测试文件自身）：0 调用方
        var repoRoot = FindRepoRoot();
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoRoot, file);
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("archives"))
                continue;
            if (rel.EndsWith("DmsDatabaseServiceDeadCodeTests.cs", StringComparison.Ordinal))
                continue;   // 本文件自身含方法名（字符串断言），不算调用方
            // 只看代码（剥掉注释）——注释里可以写"该方法已删除"的历史说明
            if (UniFlow.Tests.Common.SourceText.CodeOf(file).Contains("GetUnknownPatientOrdersAsync"))
                hits.Add(rel);
        }

        hits.ShouldBeEmpty("M-01：GetUnknownPatientOrdersAsync 不应有任何调用方残留");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "code", "src", "UniFlow")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root not found");
    }
}

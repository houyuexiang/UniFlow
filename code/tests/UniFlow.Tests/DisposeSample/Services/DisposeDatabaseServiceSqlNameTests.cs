using Shouldly;
using UniFlow.DisposeSample.Services;

namespace UniFlow.Tests.DisposeSample.Services;

/// <summary>
/// M-05：<c>SanitizeSqlName</c> 的白名单校验用例。
/// <para>
/// 历史缺陷：旧实现用正则<em>剔除</em>非法字符且**允许反引号**
/// （<c>[^a-zA-Z0-9_`]</c>）→ 反引号可被拼进 SQL（注入面）；
/// 且"剔除"语义会把 <c>view over</c> 静默变成 <c>viewover</c>（查到另一个视图，排查不到）。
/// 现在：整名不匹配 <c>^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)?$</c> 即**拒绝**（返回 null），不做字符级修补。
/// </para>
/// <para>
/// 复核 D4：原规则 <c>^[A-Za-z0-9_]+$</c> 会把现场带 schema 前缀的
/// <c>CommandName</c>（如 <c>flexlab.view_x</c>）整个拒掉 → 丢弃功能停摆。
/// 现允许**可选的一个** schema 前缀。
/// </para>
/// </summary>
public class DisposeDatabaseServiceSqlNameTests
{
    [Theory]
    [InlineData("view_overtimestoragesample")]
    [InlineData("view1")]
    [InlineData("VIEW_ABC_123")]
    [InlineData("_leading_underscore")]
    public void SanitizeSqlName_ValidName_IsAccepted(string name)
    {
        DisposeDatabaseService.SanitizeSqlName(name).ShouldBe(name);
    }

    [Theory]
    [InlineData("flexlab.view_x")]                 // 现场 schema 前缀 → 必须放行（复核 D4）
    [InlineData("flexlab.view_overtimestoragesample")]
    [InlineData("db1.view1")]
    public void SanitizeSqlName_SchemaPrefixedName_IsAccepted(string name)
    {
        // 允许**一个** schema 前缀；名字原样返回（不改写、不剔除字符）
        DisposeDatabaseService.SanitizeSqlName(name).ShouldBe(name);
    }

    [Theory]
    [InlineData("view`over")]          // 反引号 → 注入面，必须拒
    [InlineData("view`")]
    [InlineData("`")]
    [InlineData("view;drop")]          // 分号
    [InlineData("view over")]          // 空格
    [InlineData("view-over")]          // 连字符
    [InlineData("view\"over")]
    [InlineData("view'over")]
    [InlineData("view\nnewline")]
    [InlineData("视图")]                 // 非 ASCII 字母
    [InlineData("a.b.c")]               // 只允许**一个**点（schema 前缀），多点仍拒
    [InlineData(".view")]               // 前导点
    [InlineData("view.")]               // 尾随点
    [InlineData(".")]
    [InlineData("flexlab.view over")]   // schema 前缀 + 非法字符
    [InlineData("flexlab.view`over")]
    [InlineData("flex-lab.view_x")]     // schema 名本身含连字符
    [InlineData("flex lab.view_x")]    // schema 名本身含空格
    public void SanitizeSqlName_InvalidName_IsRejected(string name)
    {
        // 拒绝 = 返回 null（调用方据此拒绝执行，不发任何 SQL）
        DisposeDatabaseService.SanitizeSqlName(name).ShouldBeNull();
    }

    [Fact]
    public void SanitizeSqlName_EmptyName_FallsBackToDefaultView()
    {
        // 空名保留既有默认行为（回退默认视图），不是拒绝
        DisposeDatabaseService.SanitizeSqlName("").ShouldBe(DisposeDatabaseService.DefaultDisposeViewName);
        DisposeDatabaseService.SanitizeSqlName(null).ShouldBe(DisposeDatabaseService.DefaultDisposeViewName);
    }

    [Fact]
    public void SanitizeSqlName_OldBehaviour_WouldHaveLetBacktickThrough()
    {
        // 记录旧行为对照：旧实现（剔除非法字符 + 允许反引号）会把反引号原样留下 → 注入面。
        // 新实现必须拒绝整名（不是剔除）。
        var old = System.Text.RegularExpressions.Regex.Replace("view`over", @"[^a-zA-Z0-9_`]", "");
        old.ShouldBe("view`over", "旧实现保留了反引号（历史缺陷对照）");

        DisposeDatabaseService.SanitizeSqlName("view`over").ShouldBeNull();
    }

    [Fact]
    public void SanitizeSqlName_StrictRule_WouldHaveRejectedSchemaPrefix()
    {
        // 复核 D4 的历史缺陷对照：收紧版规则（不允许 schema 前缀）会拒掉现场常见写法
        var strict = new System.Text.RegularExpressions.Regex("^[A-Za-z0-9_]+$");
        strict.IsMatch("flexlab.view_x").ShouldBeFalse("收紧版规则会拒掉 schema 前缀（历史缺陷对照）");

        // 现在的规则必须放行（否则现场 CommandName 带 schema 前缀时丢弃功能停摆）
        DisposeDatabaseService.SanitizeSqlName("flexlab.view_x").ShouldBe("flexlab.view_x");
    }
}

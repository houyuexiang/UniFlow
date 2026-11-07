using System.Text.RegularExpressions;
using Shouldly;

namespace UniFlow.Tests.Deploy;

/// <summary>
/// Windows 服务安装脚本的路径用例（P3）。
/// <para>
/// 历史缺陷：<c>$SrcDir = Join-Path $PSScriptRoot "..\src"</c> → 实际指向
/// <c>&lt;repo&gt;\deploy\src</c>（不存在），源码真实位置是 <c>&lt;repo&gt;\code\src</c>
/// → <c>dotnet publish</c> 必然失败。
/// 现在：<c>$SrcDir</c> 指向 <c>&lt;repo&gt;\code\src</c>，<c>$OutDir</c> 指向
/// <c>&lt;repo&gt;\release\publish</c>（与 deploy/build.sh、.gitignore 一致）。
/// </para>
/// </summary>
public class InstallServiceScriptPathTests
{
    private static string RepoFile(params string[] relativeParts)
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

    /// <summary>取脚本里 "$Var = Join-Path $PSScriptRoot "&lt;相对路径&gt;" 的相对路径。</summary>
    private static string ReadScriptRelativePath(string scriptPath, string varName)
    {
        var text = File.ReadAllText(scriptPath);
        var pattern = @"\$\b" + Regex.Escape(varName) + @"\b\s*=\s*Join-Path\s+\$PSScriptRoot\s+""([^""]+)""";
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        m.Success.ShouldBeTrue($"脚本里应能找到 ${varName} = Join-Path $PSScriptRoot \"...\"");
        return m.Groups[1].Value;
    }

    /// <summary>按 Windows 语义解析相对路径（脚本里是反斜杠）。</summary>
    private static string Resolve(string scriptDir, string relative)
        => Path.GetFullPath(Path.Combine(scriptDir, relative.Replace('\\', Path.DirectorySeparatorChar)));

    [Fact]
    public void SrcDir_Resolves_To_Existing_ProjectFile()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var scriptDir = Path.GetDirectoryName(script)!;
        var srcDir = Resolve(scriptDir, ReadScriptRelativePath(script, "SrcDir"));

        // 源码目录必须真实存在，且 csproj 可找到（否则 dotnet publish 必然失败）
        Directory.Exists(srcDir).ShouldBeTrue($"源码目录不存在: {srcDir}");
        var csproj = Path.Combine(srcDir, "UniFlow", "UniFlow.csproj");
        File.Exists(csproj).ShouldBeTrue($"项目文件不存在: {csproj}");

        // 旧写法（..\src → deploy\src）指向的目录不存在
        Directory.Exists(Path.Combine(scriptDir, "src")).ShouldBeFalse("deploy\\src 不应存在（那是旧的错误路径）");
    }

    [Fact]
    public void OutDir_Resolves_To_Release_Publish()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var scriptDir = Path.GetDirectoryName(script)!;
        var outDir = Resolve(scriptDir, ReadScriptRelativePath(script, "OutDir"));

        // 与 deploy/build.sh（release/publish）和 .gitignore（release/publish/）一致
        outDir.Replace('\\', '/').ShouldEndWith("/release/publish");
        var repoRoot = Directory.GetParent(scriptDir)!.Parent!.FullName;
        outDir.ShouldBe(Path.GetFullPath(Path.Combine(repoRoot, "release", "publish")));
    }

    [Fact]
    public void Script_Publishes_The_Real_Csproj()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var text = File.ReadAllText(script);

        // 不能再用 "Join-Path $SrcDir ..." 拼项目名（SrcDir 已含 code\src，拼接方式必须与之一致）
        text.ShouldContain("$Csproj = Join-Path $SrcDir \"$Project\\$Project.csproj\"");
        text.ShouldContain("dotnet publish $Csproj");
        // 找不到源码时必须明确报错退出（而不是静默继续）
        text.ShouldContain("Source project not found");
    }
}

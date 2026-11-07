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

    // ===== M-02：install 分支必须在 exe 已存在时**仍然 publish** =====
    // 历史缺陷：旧脚本 `if (-not (Test-Path $exePath)) { $out = Publish-Service ... }`
    //           → 目标 exe 已存在就跳过 publish → 现场升级时装的仍是旧二进制。

    /// <summary>取脚本里 "install" 分支的源码块（到下一个分支为止）。</summary>
    private static string ReadInstallBranch(string scriptPath)
    {
        var text = File.ReadAllText(scriptPath);
        var start = text.IndexOf("\"install\" {", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, "脚本里应能找到 \"install\" 分支");
        var end = text.IndexOf("\"uninstall\" {", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, "脚本里应能找到 \"uninstall\" 分支（install 分支的结束点）");
        return text.Substring(start, end - start);
    }

    [Fact]
    public void InstallBranch_DoesNot_SkipPublish_When_ExeAlreadyExists()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var branch = ReadInstallBranch(script);

        // 旧缺陷模式必须消失：**不能**存在"exe 不存在才 publish"的守卫
        // （即：条件判断 Test-Path $exePath 的分支体里调用 Publish-Service）。
        Regex.IsMatch(branch,
            @"if\s*\(\s*-not\s*\(\s*Test-Path\s+\$exePath\s*\)\s*\)\s*\{[^}]*Publish-Service",
            RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .ShouldBeFalse("install 分支不得再以“exe 是否存在”作为 publish 的前置条件");
        // publish 调用必须存在
        branch.ShouldContain("Publish-Service");
    }

    [Fact]
    public void InstallBranch_Publishes_ByDefault()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var text = File.ReadAllText(script);
        var branch = ReadInstallBranch(script);

        // 默认路径（不传 -SkipPublish）走 publish 分支
        branch.ShouldContain("if ($SkipPublish)");
        // 非 skip 分支里必须调用 publish
        var elsePart = branch.Substring(branch.IndexOf("if ($SkipPublish)", StringComparison.Ordinal));
        elsePart.ShouldContain("$out = Publish-Service");
        // 脚本必须真的声明了这个开关
        text.ShouldContain("[switch]$SkipPublish");
    }

    [Fact]
    public void SkipPublish_PrintsVersionWarning_AndRefusesWhenBinaryMissing()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var branch = ReadInstallBranch(script);

        // 跳过 publish 时必须打印版本警告（否则等于默默装旧程序）
        branch.ShouldContain("WARNING: -SkipPublish specified");
        branch.ShouldContain("binary version");
        branch.ShouldContain("project version");
        // 没有已有二进制时必须拒绝（不能装出一个不存在的服务）
        branch.ShouldContain("no existing binary was found");
        branch.ShouldContain("exit 1");
    }

    [Fact]
    public void InstallBranch_Verifies_PublishedExe_Exists()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var branch = ReadInstallBranch(script);

        // publish 之后必须校验 exe 真的存在（否则 New-Service 会指向空路径）
        branch.ShouldContain("Publish did not produce the expected executable");
    }

    // ===== M-02 复核 D3：publish 失败必须被检查并中止安装 =====
    // 历史缺陷：`& dotnet publish ...` 之后**不检查 $LASTEXITCODE** →
    //   publish 失败时旧 exe 仍在 → Test-Path $exePath 通过 →
    //   继续 New-Service → 把**旧二进制**装成服务（还打印 "Published … version"）。

    /// <summary>取脚本里 Publish-Service 函数体（到 switch 之前）。</summary>
    private static string ReadPublishFunction(string scriptPath)
    {
        var text = File.ReadAllText(scriptPath);
        var start = text.IndexOf("function Publish-Service", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, "脚本里应能找到 Publish-Service 函数");
        var end = text.IndexOf("switch ($Action)", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, "脚本里应能找到 switch ($Action)（函数体的结束点）");
        return text.Substring(start, end - start);
    }

    /// <summary>取 "if (cond) { ... }" 的花括号块（含首尾花括号，按花括号配平）。</summary>
    private static string ReadBraceBlock(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(openBraceIndex, i - openBraceIndex + 1);
            }
        }
        throw new InvalidOperationException("花括号块没有闭合");
    }

    /// <summary>取 publish 失败分支（"$publishExitCode -ne 0" 那个 if 的花括号块）。</summary>
    private static string ReadPublishFailureBlock(string scriptPath)
    {
        var fn = ReadPublishFunction(scriptPath);
        var condIdx = fn.IndexOf("$publishExitCode -ne 0", StringComparison.Ordinal);
        condIdx.ShouldBeGreaterThan(-1, "脚本里应能找到 publish 退出码判断");
        var open = fn.IndexOf('{', condIdx);
        open.ShouldBeGreaterThan(-1, "退出码判断后应有花括号块");
        return ReadBraceBlock(fn, open);
    }

    [Fact]
    public void PublishService_Checks_LastExitCode_AndAbortsOnFailure()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var fn = ReadPublishFunction(script);

        // 必须读 $LASTEXITCODE 并对非零退出码做判断
        fn.ShouldContain("$LASTEXITCODE");
        Regex.IsMatch(fn, @"if\s*\(\s*\$publishExitCode\s*-ne\s*0\s*\)", RegexOptions.IgnoreCase)
            .ShouldBeTrue("publish 后必须判断退出码（旧实现不检查 $LASTEXITCODE → publish 失败也继续装旧二进制）");

        // 失败分支必须以非零退出结束（不能只打个 warning 就继续）
        var failBlock = ReadPublishFailureBlock(script);
        failBlock.ShouldContain("exit 1");
        failBlock.ShouldContain("FAILED");
    }

    [Fact]
    public void PublishFailure_Cannot_Reach_NewService_Or_StartService()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var failBlock = ReadPublishFailureBlock(script);

        // 失败分支**自己**必须 exit 1（否则脚本会带着失败继续往下装服务）
        failBlock.ShouldContain("exit 1");
        // 失败分支里不得出现装服务/起服务的动作
        failBlock.ShouldNotContain("New-Service");
        failBlock.ShouldNotContain("Start-Service");
        failBlock.ShouldNotContain("sc.exe failure");
    }

    [Fact]
    public void PublishService_Publishes_To_StagingDir_Not_The_InstallDir()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var fn = ReadPublishFunction(script);

        // publish 必须落到**临时目录**：失败时不会覆盖正式目录里的旧 exe
        fn.ShouldContain(".staging");
        fn.ShouldContain("-o $stagingDir");
        fn.ShouldNotContain("-o (Join-Path $OutDir $Project)");
    }

    [Fact]
    public void InstallBranch_Installs_The_FreshlyPublished_Output()
    {
        var script = RepoFile("deploy", "windows", "install-service.ps1");
        var branch = ReadInstallBranch(script);

        // 正式目录必须用**本次** publish 的产物替换（否则可能装上残留的旧文件）
        branch.ShouldContain("Move-Item $stagedDir $finalDir");
        branch.ShouldContain("Remove-Item $finalDir -Recurse -Force");
        // 替换后必须再确认 exe 存在
        branch.ShouldContain("Publish did not produce the expected executable");
        // 替换失败必须中止（不能带着半个目录继续 New-Service）
        branch.ShouldContain("Failed to replace the published output directory");
    }
}

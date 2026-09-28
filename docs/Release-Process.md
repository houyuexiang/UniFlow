# UniFlow Release 流程

## 版本号规则

- 主版本.次版本.修订号（如 `1.0.1`）
- 主版本：重大架构变更
- 次版本：功能新增
- 修订号：Bug 修复

## 版本号自动递增

发布时可通过 `git tag` 自动管理版本号，无需手动修改项目文件：

```bash
# 查看当前最新 tag
git describe --tags --abbrev=0

# 手动打 tag 发布
VERSION="1.0.1"
git tag -a v${VERSION} -m "v${VERSION}"
git push origin v${VERSION}
```

以后每次发布只需递增 tag 即可（如 `1.0.2`、`1.1.0`、`2.0.0`），
`build.sh` 中的 `VERSION` 变量和 `.csproj` 中的 `<Version>` 可根据需要同步更新。

## 手动发布步骤

### 1. 构建 + 打包（推荐一键）

```bash
# 默认用仓库 code/src/UniFlow/appsettings.json
./deploy/package.sh

# 交付现场配置（打进包内，不覆盖现场已有 appsettings.json）
APP_SETTINGS=/path/to/site-appsettings.json ./deploy/package.sh

# 版本号默认取 UniFlow.csproj 的 <Version>，可覆盖
VERSION=1.0.6.5 OUT=release ./deploy/package.sh
```

产物（输出目录默认 `release/`）：

```
release/UniFlow-<VERSION>-linux-x64.tar.gz   # 顶层目录 uniflow/
release/UniFlow-<VERSION>-win-x64.zip        # 顶层目录 uniflow/
```

`package.sh` 内部即单文件自包含发布（Linux/Windows 双平台），关键参数：

```bash
dotnet publish UniFlow/UniFlow.csproj \
  --configuration Release --self-contained true \
  --runtime <linux-x64|win-x64> \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \  # SQLite 原生库内嵌，安装包无需单独提供
  -o <输出目录>
```

> 单测：`dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj`（需 .NET 10 SDK；
> `deploy/build.sh test` 走 docker SDK 镜像）。

### 2. 发布到 GitHub

```bash
VERSION="1.0.6.5"

# 打 Tag
git tag -a v${VERSION} -m "v${VERSION} - 版本说明"
git push origin v${VERSION}

# 创建 Release
gh release create v${VERSION} \
  --title "UniFlow v${VERSION}" \
  --notes "版本说明"

# 上传附件（package.sh 产物）
gh release upload v${VERSION} \
  release/UniFlow-${VERSION}-linux-x64.tar.gz \
  release/UniFlow-${VERSION}-win-x64.zip
```

## 发布清单

- [ ] 所有测试通过 (`./deploy/build.sh test` 或 `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj`)
- [ ] `UniFlow.csproj` 版本号已递增
- [ ] `deploy/build.sh` 的 `VERSION` 已同步
- [ ] 配置/改动记录文件已更新 (`docs/CHANGELOG-<VERSION>.md`)
- [ ] `./deploy/package.sh` 已产出 Linux / Windows 双平台安装包
- [ ] Tag 已推送
- [ ] Release 已创建并上传附件
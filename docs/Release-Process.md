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

### 2. 打包产物自检（必做，防漏文件）

历史教训：**1.0.5.5 ~ 1.0.6.5 的 Linux 包漏了 `README.md` / `INSTALL.md`**（`package.sh` Linux 段漏 `cp`，直到 1.0.6.5 才修复）。每次打包后必须跑：

```bash
VERSION="1.0.6.5"
# 两包文档清单齐备（各 3 份：README.md / INSTALL.md / CONFIG-REFERENCE.md）
tar -tzf release/UniFlow-${VERSION}-linux-x64.tar.gz | grep -E '\.md$'
unzip -l release/UniFlow-${VERSION}-win-x64.zip | grep -E '\.md$'
# 平台共用文档逐字节一致
mkdir -p /tmp/pkgchk/{linux,win}
tar -xzf release/UniFlow-${VERSION}-linux-x64.tar.gz -C /tmp/pkgchk/linux uniflow/INSTALL.md uniflow/CONFIG-REFERENCE.md
unzip -oq release/UniFlow-${VERSION}-win-x64.zip -d /tmp/pkgchk/win uniflow/INSTALL.md uniflow/CONFIG-REFERENCE.md
cmp /tmp/pkgchk/linux/uniflow/INSTALL.md /tmp/pkgchk/win/uniflow/INSTALL.md && echo INSTALL_OK
cmp /tmp/pkgchk/linux/uniflow/CONFIG-REFERENCE.md /tmp/pkgchk/win/uniflow/CONFIG-REFERENCE.md && echo CONFREF_OK
```

期望：两包各含 3 份 `.md`；`INSTALL_OK`、`CONFREF_OK` 均输出。
文档源文件位置：`deploy/INSTALL.md`（双平台共用）、`docs/CONFIG-REFERENCE.md`（双平台共用）、`deploy/linux/README.md` 与 `deploy/windows/README.md`（平台专属，改一处记得评估另一处是否同步）。

### 3. 发布到 GitHub

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

### 4. 同步到 NAS（现场交付源）

NAS 挂载：`//192.168.0.47/CloudNAS` → `/mnt/nasbk/UniFlow/`。惯例：双平台包 + `UniFlow-<VERSION>-改动记录.md`（+ 部署测试报告）。

```bash
cp release/UniFlow-${VERSION}-linux-x64.tar.gz release/UniFlow-${VERSION}-win-x64.zip /mnt/nasbk/UniFlow/
sync
# 校验：NAS 上 md5 与本地一致
md5sum release/UniFlow-${VERSION}-*.{tar.gz,zip} /mnt/nasbk/UniFlow/UniFlow-${VERSION}-*.{tar.gz,zip}
```

> 覆盖 NAS 旧包前若怀疑有版本差异，先改名为 `*.old-<原因>.bak` 保留，不要直接覆盖。

## 发布清单

- [ ] 所有测试通过 (`./deploy/build.sh test` 或 `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj`)
- [ ] `UniFlow.csproj` 版本号已递增
- [ ] `deploy/build.sh` 的 `VERSION` 已同步
- [ ] 配置/改动记录文件已更新 (`docs/CHANGELOG-<VERSION>.md`)
- [ ] `./deploy/package.sh` 已产出 Linux / Windows 双平台安装包
- [ ] **打包产物自检通过（第 2 步：两包各 3 份 `.md` + 共用文档 `cmp` 一致）**
- [ ] Tag 已推送
- [ ] Release 已创建并上传附件
- [ ] **安装包已同步 NAS 且 md5 校验一致，改动记录已放 NAS**
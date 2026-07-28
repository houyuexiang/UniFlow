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

### 1. 构建

```bash
# 构建 + 测试
./deploy/build.sh build
./deploy/build.sh test

# 发布 Linux 单文件
docker run --rm -v $(pwd):/project -w /project/code/src \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o /project/release/publish/linux-x64

# 发布 Windows 单文件
docker run --rm -v $(pwd):/project -w /project/code/src \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o /project/release/publish/win-x64
```

### 2. 打包

```bash
VERSION="1.0.1"

# Linux 包
mkdir -p /tmp/uniflow-linux
cp release/publish/linux-x64/UniFlow /tmp/uniflow-linux/
cp code/src/UniFlow/appsettings.json /tmp/uniflow-linux/
cp deploy/linux/uniflow.service /tmp/uniflow-linux/
cp deploy/deploy-linux.sh /tmp/uniflow-linux/
cd /tmp && zip -r $(pwd)/release/publish/UniFlow-v${VERSION}-linux-x64.zip uniflow-linux/

# Windows 包
mkdir -p /tmp/uniflow-win
cp release/publish/win-x64/UniFlow.exe /tmp/uniflow-win/
cp code/src/UniFlow/appsettings.json /tmp/uniflow-win/
cp deploy/windows/install-service.ps1 /tmp/uniflow-win/
cd /tmp && zip -r $(pwd)/release/publish/UniFlow-v${VERSION}-win-x64.zip uniflow-win/
```

### 3. 发布到 GitHub

```bash
# 打 Tag
git tag -a v${VERSION} -m "v${VERSION} - 版本说明"
git push origin v${VERSION}

# 创建 Release
gh release create v${VERSION} \
  --title "UniFlow v${VERSION}" \
  --notes "版本说明"

# 上传附件
gh release upload v${VERSION} \
  release/publish/UniFlow-v${VERSION}-linux-x64.zip \
  release/publish/UniFlow-v${VERSION}-win-x64.zip
```

## 发布清单

- [ ] 所有测试通过 (`./deploy/build.sh test`)
- [ ] 配置文件已更新 (`appsettings.json`)
- [ ] 更新日志文件已更新
- [ ] Linux / Windows 双平台发布包已构建
- [ ] Tag 已推送
- [ ] Release 已创建并上传附件
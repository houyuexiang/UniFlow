# UniFlow Release 流程

## 手动发布步骤

### 1. 构建

```bash
# 构建 + 测试
./build.sh build
./build.sh test

# 发布 Linux 单文件
docker run --rm -v $(pwd):/project -w /project/src \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o /project/publish/linux-x64

# 发布 Windows 单文件
docker run --rm -v $(pwd):/project -w /project/src \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o /project/publish/win-x64
```

### 2. 打包

```bash
VERSION="10.0.1"

# Linux 包
mkdir -p /tmp/uniflow-linux
cp publish/linux-x64/UniFlow /tmp/uniflow-linux/
cp src/UniFlow/appsettings.json /tmp/uniflow-linux/
cp deploy/linux/uniflow.service /tmp/uniflow-linux/
cp deploy/deploy-linux.sh /tmp/uniflow-linux/
cd /tmp && zip -r project/publish/UniFlow-v${VERSION}-linux-x64.zip uniflow-linux/

# Windows 包
mkdir -p /tmp/uniflow-win
cp publish/win-x64/UniFlow.exe /tmp/uniflow-win/
cp src/UniFlow/appsettings.json /tmp/uniflow-win/
cp deploy/windows/install-service.ps1 /tmp/uniflow-win/
cd /tmp && zip -r project/publish/UniFlow-v${VERSION}-win-x64.zip uniflow-win/
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
  publish/UniFlow-v${VERSION}-linux-x64.zip \
  publish/UniFlow-v${VERSION}-win-x64.zip
```

## 版本号规则

- 主版本.次版本.修订号
- 主版本：重大架构变更
- 次版本：功能新增
- 修订号：Bug 修复

## 发布清单

- [ ] 所有测试通过 (`./build.sh test`)
- [ ] 配置文件已更新 (`appsettings.json`)
- [ ] 更新日志文件已更新
- [ ] Linux / Windows 双平台发布包已构建
- [ ] Tag 已推送
- [ ] Release 已创建并上传附件
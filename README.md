# UniFlow

实验室自动化流程协调服务，基于 .NET 10 构建，支持 Windows Service 和 Linux systemd 部署。

## 项目结构

```
UniFlow/
├── archives/         历史资源（Delphi 旧项目参考）
├── code/             源代码
│   ├── src/          主项目 + AccessAgent
│   └── tests/        单元测试
├── deploy/           部署脚本（Docker / systemd / Windows Service）
├── release/          发布产物
└── docs/             文档
```

## 功能模块

| 模块 | 说明 |
|------|------|
| DisposeSample | 样本丢弃自动化流程 |
| SrmExport | SRM 数据导出 |
| WorkListCleaner | 工作清单清理（支持 Immulite） |
| DMSAutoOrder | DMS 自动下单 |
| WebAdmin | Web 管理界面 + 健康检查 |

## 快速开始

```bash
# 构建
./deploy/build.sh build

# 测试
./deploy/build.sh test

# Docker 部署
./deploy/build.sh docker
docker compose -f deploy/docker-compose.yml up -d
```

## 发布

版本号格式 `主版本.次版本.修订号`，发布时通过 `git tag` 管理：

```bash
git tag -a v1.0.1 -m "v1.0.1"
git push origin v1.0.1
gh release create v1.0.1 --title "UniFlow v1.0.1"
```

详见 [Release-Process.md](docs/Release-Process.md)。

## 系统要求

- .NET 10 SDK（构建）
- Docker（可选，容器部署）
- Windows Server 2012+ / Linux（运行）
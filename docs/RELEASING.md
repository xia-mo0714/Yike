# Windows 发布与检查更新

Windows 独立仓库：`xia-mo0714/Yike`。正式版本使用 `v2.3.1` 等递增标签，安装包统一命名 `Yike-Setup.exe`。不要只替换同版本附件来推送更新。

客户端从 Releases 列表选择版本号最高的非草稿、非预发布 Windows 安装包，验证 GitHub 返回的安装包大小和 SHA-256 后才启动安装。断网或解析失败会明确提示无法确认最新版。

## 发布步骤

1. 同步更新 `src/Application/AssemblyInfo.cs`、`installer/Setup.cs` 和 `assets/update-feed.json` 的版本；模型下载地址必须指向实际存在的模型附件。
2. 在 Windows 准备运行库，执行 `build.ps1` 和 `verify.ps1`；原生语音候选验收未通过时保持生产开关关闭。
3. 用 `installer/build-installer.ps1` 生成轻量安装包，运行 `Yike-Setup.exe --verify --silent` 验证内嵌文件。
4. 创建草稿 Release，上传 `Yike-Setup.exe`、`SHA256SUMS.txt`、`update-windows.json`。首次模型发布还需上传校验通过的 `ggml-small-q8_0.bin`（SHA-256：`49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f`）。
5. 全部附件上传完成、大小与 SHA-256 核对一致后发布。不得发布空附件或只有网页链接的更新清单。
6. 从独立测试目录运行新客户端的 `--update-service-test --download-update-test`，验证真实发现版本、下载和校验。最后由保留的旧版安装测试实际升级。

CI 的 core artifact 不包含完整语音运行库，不能充当正式安装包。安装包和模型放在 Releases，不放进源码提交；保持第三方许可证随安装包交付。

## HTTPS 服务器清单

国内直连下载需将安装包和模型部署到自有 HTTPS 服务器，在 `update-windows.json` 使用实际安装包地址。字段为 `Version`、`DownloadUrl`、`ReleaseUrl`、`Notes`、`Sha256`、`Size`。

客户端可通过安装目录内 `update-source.txt` 指定 HTTPS 清单地址；这个文件是本机配置，不应提交。当前发布没有自动部署或修改云服务器，也没有承诺 GitHub 在国内无需代理可达。

## 旧仓库迁移限制

旧 2.1/2.3 客户端写死了 `a17746168234-alt/Yike` 或此前的服务器清单，不会因为新仓库创建而自动改查新地址。本次未修改本机旧安装。

要迁移旧用户，需在原更新源发布兼容迁移版本/清单，或手动安装一次新仓库的 2.3.1。此后客户端读取新仓库。

账号与赠送额度翻译仍使用现有账号服务，与更新下载通道相互独立。

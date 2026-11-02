# 打包产物

此目录用于存放本地发布与打包产物。除本说明外，内容均由 `.gitignore` 忽略，不应提交到仓库。

发布脚本默认输出到：

```text
artifacts/publish/<模式>/<RID|portable>/
```

例如：

```text
artifacts/publish/framework/linux-x64/
artifacts/publish/single/win-x64/
artifacts/publish/aot/linux-x64/
artifacts/publish/aot-static/linux-x64/
```

可用 `--output` / `-Output` 指定其他位置。

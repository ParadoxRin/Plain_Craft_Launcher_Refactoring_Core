# 0.36.1 清洁基线与交接

新私有仓库：ParadoxRin/Plain-Craft-Launcher-Refactoring。main从一个0.36.1初始提交开始，标签v0.36.1；不继承旧PCL_Mac的提交、标签、reflog或bundle。Git作者使用GitHub noreply地址。

克隆新仓库或解压新的带Git交接包后，先运行git status、git log --all --oneline和git fsck --full。后续正常提交，不合并旧仓库历史。保留的技术记录提供历史背景，不构成旧版本二进制或本版测试证据。

构建使用scripts/build-handoff.sh，验证见开发与验证.md及0.36.1报告。源码包不包含账户、世界存档、Java/Minecraft资产、SDK、OAuth应用标识、密钥或个人测试日志。开发者必须准备自己的隔离测试数据。

应用包为ad-hoc签名，未公证。源码仍为私有，尚未满足PCL指南的公开源码要求。后续公开前需再次审计，并逐项检查许可要求。

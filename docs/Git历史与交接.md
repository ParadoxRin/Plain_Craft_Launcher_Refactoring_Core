# Git 历史与交接（0.41.0）

当前公开仓库：https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core 。这是原Plain-Craft-Launcher-Refactoring仓库更名后的地址，沿用main和既有标签，不新建或重写历史。

Git/隐私基线从单个0.36.1初始提交与v0.36.1标签开始，不继承旧PCL_Mac的提交、reflog、标签或bundle。作者使用GitHub noreply地址。源码中的历史技术文档保留当时背景，不等于旧二进制或本版重新实测；缺失的历史证据明确登记。

优先克隆仓库获取历史，运行git status、git log --all --oneline和git fsck --full。Release源码ZIP为指定提交的git archive，不含.git；需要继续保留提交历史时使用clone。不要把旧交接包合并回当前历史。

0.41.0构建与验收见开发与验证.md及发布报告。源码/应用包不包含个人账户、钥匙串、世界、Java/Minecraft资产、SDK缓存或原始敏感日志；公开OAuth client ID自0.40.1起按所有者授权内置，不是用户令牌或client secret。原“默认空ID/私有仓库”的文档只适用于对应历史版本。

本项目原创且有权授权的内容采用MIT，PCL及第三方内容保留原许可。仓库已公开，不再沿用“源码未公开”的当前状态提示；其他分发约束、ad-hoc签名和未公证事实仍保留。

"""Audit every original field against executable Mac bindings; retain unsupported and pending entries."""
from pathlib import Path
import json
from collections import Counter
root = Path(__file__).resolve().parents[1]
baseline = json.loads((root / 'docs/windows-layout-baseline.json').read_text())
rows = []

def classify(page, card, label):
    if page == 'PageSetupLaunch':
        if label == '自定义信息':
            return ('直接绑定', 'LaunchSettings.VersionInformation → version_type 启动参数')
        if label == '默认版本隔离':
            return ('平台固定策略', '各实例始终隔离，未提供共享目录模式')
        if label == '游戏窗口标题':
            return ('未完成', 'GLFW 窗口标题修改尚未实现，不将 Dock 名称冒充窗口标题')
        if label == '进程优先级':
            return ('未完成', '未实现 macOS 进程优先级策略')
        if label == '启动器可见性':
            return ('部分绑定', '支持隐藏后恢复与最小化；未实现启动后关闭两种策略')
        if label == '游戏窗口':
            return ('部分绑定', '全屏与自定义宽高接入启动参数；未实现最大化与启动器尺寸联动')
        if card == '内存分配':
            if label == '启动游戏前进行内存优化':
                return ('平台差异', '不执行 Windows 工作集清理')
            return ('直接绑定', 'AutomaticMemory / MinimumMemoryMb / MaximumMemoryMb → LaunchRuntimePolicy')
        if card == '离线皮肤':
            if label in ('自定义', '保存皮肤', '更改皮肤', '重置皮肤'):
                return ('现有功能入口', '同卡片打开 SkinManagerWindow，使用 OfflineSkinService；未改成原版独立控件')
            return ('未完成', '尚未绑定原版预设皮肤或玩家名抓取控件')
        if label.startswith('启动 Minecraft 时使用的额外'):
            return ('直接绑定', 'JvmArguments → 实际 Java 参数；禁止覆盖内存与入口')
        if label == '游戏参数':
            return ('直接绑定', 'GameArguments → 游戏参数；账户与目录受保护')
        if label == '启动前执行命令':
            return ('直接绑定', 'LaunchHookSettings / LaunchHookRunner，本地程序与参数分开设置')
        if label == '内存管理':
            return ('部分绑定', '默认、G1、ZGC、分代 ZGC；未提供原版调优 G1 预设')
        if label == '在正版登录时验证 SSL 证书':
            return ('固定安全行为', '持续验证 TLS 证书，不提供关闭验证开关')
        if label == '禁用 LWJGL Unsafe Agent':
            return ('未完成', '尚未接入该 Agent，不提供无效禁用开关')
        return ('平台差异', 'Windows Java Launch Wrapper / 显卡注册表操作不用于 Mac')
    if page == 'PageSetupLink':
        return ('替代实现入口', 'EasyTier 房间窗口与网络检查可用，未接入 PCL 原版节点服务或协议')
    if page == 'PageSetupUI':
        if card == '基础':
            if label == '不透明度':
                return ('直接绑定', 'WindowOpacity → 实际 Window.Opacity')
            if label in ('滑稽彩', '欧皇彩', '打开启动器时显示 PCL 图标', '支持 PCL 开发！'):
                return ('未完成', '原版动态彩色/启动动画/赞助流程未移植')
            return ('直接绑定', 'Accent → 实际标题栏颜色；静态配色预设与自定义颜色，不继承赞助解锁状态')
        if card == '背景图片':
            if label in ('不透明度', '背景模糊'):
                return ('直接绑定', 'BackgroundImage 透明度 / BlurEffect')
            if label == '自适应方式':
                return ('部分绑定', '填满、完整显示、拉伸、原尺寸居中；其余锚点和平铺未实现')
            if label in ('打开文件夹', '刷新背景图片'):
                return ('替代实现入口', '选择本地图片并保存，立即加载；未实现随机背景目录')
            return ('未完成', '尚未实现独立叠加彩色背景控制')
        if card == '背景音乐':
            if label in ('音量', '打开启动器自动开始播放', '游戏启动后自动暂停播放，游戏退出后自动开始播放'):
                return ('直接绑定', 'MusicVolume / MusicAutoPlay / PauseMusicInGame → LocalMusicPlayer')
            if label in ('打开文件夹', '刷新背景音乐'):
                return ('替代实现入口', '本地音频选择、试听和停止；不提供文件夹播放列表')
            return ('未完成', '随机播放列表与仅游戏中播放策略尚未实现')
        if card == '标题栏':
            if label in ('无', '默认', '文本', '标题栏文本'):
                return ('直接绑定', 'TitleMode 与 Title → 标题栏及系统窗口标题')
            return ('未完成', '标题栏图片未实现')
        if card == '主页':
            if label in ('读取本地文件', '联网更新', '下载地址', '刷新主页', '生成教学文件'):
                return ('部分绑定', 'JSON 本地/HTTPS 文档、启动页展示与刷新；不执行 Windows XAML')
            if label == '空白':
                return ('部分绑定', '清空地址移除自定义主页，保留版本与运行状态')
            return ('未完成', '原版第三方 XAML 预设与教程/文件夹入口未移植')
        if card == '功能隐藏':
            if label in ('下载', '更多', '联机', '帮助', '关于与鸣谢', '百宝箱', '反馈', '新功能投票'):
                return ('部分绑定', 'HiddenRoutes → 顶栏或侧栏；设置/启动始终保留，两个原版联机位置未完全等价')
            return ('未完成', '未实现该原版细粒度隐藏项；不提供无效开关')
    if page == 'PageSetupSystem':
        if label in ('最大线程数', '速度限制'):
            return ('直接绑定', 'DownloadTasks.Configure → 实际并发与限速（并发以文件计）')
        if label == '文件下载源':
            return ('部分绑定', '配置 HTTPS 镜像与目标主机，按官方哈希校验并决定来源顺序')
        if label in ('版本列表源', '来源'):
            return ('未完成', '版本元数据不切换镜像；社区源通过下载页面现有工作台选择')
        if label in ('文件名格式', 'Mod 管理样式'):
            return ('未完成', '保留发布者文件名与现有本地管理样式')
        if label == '自动设置为中文':
            return ('直接绑定', 'FirstLaunchChinese → PrepareGameLanguage，保留已有语言')
        if label in ('正式版更新提示', '测试版更新提示'):
            return ('部分绑定', '可主动加载版本列表；没有后台版本更新通知')
        if label in ('导出设置', '导入设置'):
            return ('直接绑定', 'AppearanceService.Export/Import，去除账户、命令和机器路径')
        if label == '缓存文件夹':
            return ('替代实现入口', '可打开缓存检查与回收站；不支持任意移动活动缓存根目录')
        if label == '匿名数据收集':
            return ('固定行为', '本移植不收集匿名统计')
        if label in ('启动器更新', '启动器公告', '检查更新', '输入土豆码', '复制识别码'):
            return ('外部服务未接入', '没有签名更新源、原版公告/赞助身份服务；不伪造响应')
        return ('未完成', '保留日志诊断入口；原版专用调试开关尚未实现')
    return ('未完成', '未接入')
for (page, cards) in baseline['pages'].items():
    for card in cards:
        for (index, field) in enumerate(card['fields']):
            (state, implementation) = classify(page, card['title'], field['label'])
            rows.append(dict(page=page, card=card['title'], index=index, label=field['label'], status=state, implementation=implementation))
counts = dict(Counter((r['status'] for r in rows)))
(root / 'docs/0.21-settings-binding-audit.json').write_text(json.dumps(dict(total=len(rows), counts=counts, fields=rows), ensure_ascii=False, indent=2))
lines = ['# 0.21 原版设置逐项核对', '', '每一行对应 0.20 清单中的原版控件；部分绑定与替代入口不视为原版完全实现。实测边界见《0.21实现与验收.md》。', '', '| 页面 / 卡片 | 原版项 | 当前状态 | 实现或缺口 |', '|---|---|---|---|']
for r in rows:
    lines.append('| ' + ' | '.join((str(v).replace('|', ' / ').replace('\n', ' ') for v in (r['page'] + ' / ' + r['card'], r['label'], r['status'], r['implementation']))) + ' |')
(root / 'docs/【已弃用】0.21设置逐项核对.md').write_text('\n'.join(lines) + '\n')
print(len(rows), counts)

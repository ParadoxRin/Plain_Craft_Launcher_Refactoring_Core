"""Record concrete 0.22 bindings and limitations against the unchanged 121-field baseline."""
from pathlib import Path
import json
from collections import Counter
root=Path(__file__).resolve().parents[1]
old=json.loads((root/'docs/0.21-settings-binding-audit.json').read_text())
rows=old['fields']
def setrows(indices,status,detail):
    for i in indices:
        rows[i]['previousStatus']=rows[i]['status']
        rows[i]['status']=status
        rows[i]['implementation']=detail
setrows([1],'直接绑定','MacWindowPolicy + 本机 JVM agent 修改实际 NSWindow 标题；Java 8/21/25 实测')
setrows([4],'部分绑定','macOS 正常/低优先级实际设置 nice 0/10；不提权，未提供 Windows 的高/实时优先级')
setrows([3],'直接绑定','五种可见性行为 + 旧设置兼容；隐藏恢复/最小化/保持/退出后关闭/独立进程启动后关闭均经真实游戏测试')
setrows([5],'直接绑定','默认/自定义/启动器尺寸/最大化 + 全屏；实际窗口回执与启动参数测试')
setrows([9,10,11],'部分绑定','从已安装客户端提取随机/Steve/Alex 纹理，保存后生成实际资源包；保留 UUID，未强制更改宽臂/细臂模型')
setrows([22],'直接绑定','默认/G1/调优 G1/ZGC/分代 ZGC；Java 门槛与实际 G1 启动通过')
setrows([24],'直接绑定','固定 LWJGL Unsafe Agent 2.0 + SHA256；仅 Java 25+ 且 LWJGL 3.4.0/3.4.1 生效；禁用提前返回，实测原生内存读写和方法改写')
setrows([39,42],'部分绑定','动态彩色与极光色周期实际更新标题栏；为 Mac 独立动画，不复制原版赞助解锁/配色算法')
setrows([48],'部分绑定','启动时显示 PCL 字样浮层，时间受动画速度控制；不是原版矢量 Logo 动画')
setrows([50],'直接绑定','平铺、9 方位保持比例填满、拉伸、原尺寸居中、自动小图平铺 + 完整显示；实际 ImageBrush 属性和截图验证')
setrows([53],'直接绑定','独立彩色覆盖层随颜色和开关更新')
setrows([54,55],'直接绑定','本地背景目录选择/打开/随机读取与刷新；刷新重扫并避开上一张，实际控件验证')
setrows([57,59,61,62],'直接绑定','本地文件夹列表、随机不连续重复、刷新重扫、游戏内播放/结束停止；真实 afplay 顺序切换和停止测试')
setrows([66,68,69],'直接绑定','标题图片选择、渲染、清空和位图释放；保存后新进程恢复')
setrows([71,79],'部分绑定','3 个本地主页预设带可执行导航按钮；原版远程第三方 XAML 预设未兼容')
setrows([76,77],'直接绑定','内置本地 JSON 教程与实际主页目录打开；不宣称 WPF XAML 兼容')
setrows([80,83,84,82,86,87,88,89,90,91,92,94,95,96,97],'直接绑定','细粒度导航/管理按钮隐藏连接实际控件；设置及启动可隐藏，始终保留恢复按钮和 Ctrl+Shift+H；真实勾选保存与恢复测试')
setrows([81,85],'部分绑定','隐藏 Mac 联机设置/入口；原版 PCL 节点服务及两个 Windows 联机位置仍不完全等价')
setrows([93],'固定隐私行为','本移植账户列表不读取/显示微软邮箱；没有可切换显示的邮箱数据，不提供空开关')
setrows([99],'直接绑定','版本清单官方/12 秒失败回退/镜像优先；HTTPS 地址验证、无效 JSON 回退测试')
setrows([102],'部分绑定','Modrinth 元数据官方/镜像优先/回退；自定义兼容 v2 地址。散列反查及其他社区源尚未统一来源策略')
setrows([103,104],'部分绑定','五种真实文件命名 + 两种管理排列；安装/更新/禁用/移除实测。名称取发布名，项目译名尚未贯穿所有来源')
setrows([105,106],'直接绑定','启动及保存开关时检查清单，按 release/snapshot 比较已安装版本；发布包联网读取官方清单显示提示，关闭开关后提示消失')
setrows([117],'部分绑定','0 关闭动画，非零控制动态配色和启动浮层；尚未覆盖原版所有过渡动画')
setrows([118],'直接绑定','开关实际控制跨目录同哈希缓存复制；禁用后确认发生 HTTP 请求，路径和文件哈希校验')
setrows([119],'部分绑定','真实下载诊断日志，去掉查询参数/授权头；不是原版全部内部调试输出')
setrows([120],'直接绑定','0–5000 ms 下载延迟，取消能中断且不产生目标文件')
counts=dict(Counter(r['status'] for r in rows))
initial=[r for r in rows if r.get('previousStatus',r['status'])=='未完成']
result=dict(version='0.22',total=len(rows),counts=counts,original40=dict(Counter(r['status'] for r in initial)),fields=rows)
(root/'docs/0.22-settings-binding-audit.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
lines=['# 0.22 原版设置逐项核对','','同一组 121 个原版控件逐项追踪。部分绑定、平台替代和已实现待验收不算完全一致；本次并未将全部剩余项标记完成。','','原 40 个未完成项的现状：'+json.dumps(result['original40'],ensure_ascii=False),'','| 页面 / 卡片 | 原版项 | 当前状态 | 实现或缺口 |','|---|---|---|---|']
for r in rows: lines.append('| '+' | '.join(str(v).replace('|',' / ').replace('\n',' ') for v in [r['page']+' / '+r['card'],r['label'],r['status'],r['implementation']])+' |')
(root/'docs/【已弃用】0.22设置逐项核对.md').write_text('\n'.join(lines)+'\n')
print(json.dumps({'total':len(rows),'counts':counts,'original40':result['original40']},ensure_ascii=False))

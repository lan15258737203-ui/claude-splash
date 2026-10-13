# Claude 启动动画

双击桌面「Claude」图标：先播约 12 秒的启动动画（霓虹线条描出人物 → 上色 → 慢慢睁眼、眼睛发光 → 出现「Claude / 欢迎主人回家」），同时在后台打开 Claude，按任意键进入。

照抖音「Codex 启动动画」的效果做的，Windows 10 / 11 都能用（系统自带 .NET Framework，不用另装东西）。

## 装到另一台电脑（推荐：一行命令）

先装好 **Claude 桌面版**（claude.ai/download），然后：

1. 按键盘上的 **Win** 键，输入 `PowerShell`，打开「Windows PowerShell」（蓝色窗口，不用管理员）
2. 复制下面这一整行，在蓝色窗口里**点右键**粘贴，按**回车**

```powershell
[Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072; irm https://raw.githubusercontent.com/lan15258737203-ui/claude-splash/main/online-install.ps1 | iex
```

它会自动：下载（约 30MB）→ 放到 `D:\Claude启动动画`（没有 D 盘就放到用户文件夹）→ 在桌面建「Claude」图标 → 问你要不要试播一下。

以后这里有更新，再粘贴一次同一行就是升级。

## 另一种装法：下载 ZIP

连不上 `raw.githubusercontent.com`（上面那行报「下载失败」，或者半天没反应）时用这个：

1. 本页右上角绿色 **Code** → **Download ZIP**，解压
2. 把解压出来的整个文件夹放到一个固定的地方，比如 `D:\Claude启动动画`（别放在「下载」里，以后清理会被删掉）
3. 双击文件夹里的 **`安装到这台电脑.bat`**
   - 如果弹出蓝色的「Windows 已保护你的电脑」：点「**更多信息**」→「**仍要运行**」（从网上下载的 .bat 都会这样）

挪了文件夹位置，再运行一次安装就行。不想要动画了：把 `原版Claude快捷方式.lnk` 复制到桌面、改名「Claude」覆盖掉。

如果杀毒软件把 `ClaudeSplash.exe` 删了，再运行一次安装：它会用源代码在这台电脑上重新编译一个。

## 文件

| 文件 | 干什么的 |
|---|---|
| `ClaudeSplash.exe` | 动画程序（桌面图标指向它） |
| `online-install.ps1` | 一行命令安装用的脚本（下载 + 调 install.ps1） |
| `安装到这台电脑.bat` / `install.ps1` | 在桌面建「Claude」图标 |
| `图片.png` | 动画用的图（完全睁眼） |
| `睁眼帧\` | 12 张睁眼过渡帧（0% → 92%） |
| `人物蒙版.png` | 白 = 人物、黑 = 人物以外（只压暗人物以外） |
| `眼睛.txt` | 眼睛 / 瞳孔位置，眼睛的光打在哪靠它 |
| `ClaudeSplash.cs` + `build.ps1` | 源代码，改了以后运行 build.ps1 重新编译 |
| `说明.txt` | 详细说明：动画流程、怎么调明暗、怎么换图、壁纸开关 |

想只看动画、不打开 Claude：在文件夹地址栏输入 `cmd` 回车，再输入 `ClaudeSplash.exe --preview`。

# PicoPaste

随手写的一个 Windows 小剪贴板，纯刚需自用。

主要是我不想开一个很大的剪贴板窗口，所以把它贴在屏幕右边：平时缩起来，鼠标碰一下再出来。没什么宏大目标，也不打算做成面面俱到的软件，自己用得顺手就行。

![PicoPaste 截图](assets/picopaste.png)

目前就这些东西：

- 记住最近 4 条文本
- 点一下卡片就重新复制，会有小反馈
- 平时只在屏幕右边露出一点点，鼠标靠近自动展开
- 鼠标离开后自己收回去
- `Alt + V` 呼出，`Esc` 收起
- 有托盘菜单，支持多屏，重复打开也只会留一个进程
- 暖白和橙色的简单界面

## 怎么用

去 [Releases](https://github.com/Xiaoc7r/clipboard/releases) 下载 `PicoPaste.exe`，双击就行，不需要安装。

退出的话，在系统托盘里右键它。复制过的文字会放在：

```text
%LOCALAPPDATA%\PicoPaste\history.json
```

是明文，只适合自己的电脑。介意的话随时点“清空”。

## 自己编译

没上 Electron，也没引第三方包，就是一个原生 WinForms 小程序。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

编译完的文件在 `dist\PicoPaste.exe`。

就这样，够用就好。

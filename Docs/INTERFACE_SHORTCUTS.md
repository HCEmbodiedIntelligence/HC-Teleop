# PICO 界面快捷键

## 当前行为

- 长按右手圆圈 Home 键：由 PICO 系统执行视角复位，应用收到系统复位成功通知后，把界面重新排到眼前。隐藏中的界面也会重新显示。
- 短按 Home：保留系统菜单行为，不因失焦或恢复焦点自动复位界面。
- 两个摇杆同时按下：切换所有应用面板（控制栏、相机窗口、急停横幅）以及射线、坐标轴显隐。手柄模型和追踪保留。
- 每次切换后必须两个摇杆都松开，再次按下才会触发。长按、只松开一边再按回，不会重复切换。
- A 开启、B 关闭遥操作的原行为保留，长按 A/B/单摇杆不再复位界面。
- A/B 本地开关独立于 UDP 的待发送按键事件；未发现 PC 或连接断开时，B 仍能关闭 UDP。A+B 同按以 B 停止为准，A 保持按住不会在松开 B 后自动重新开启，需重新按 A。
- 整体隐藏保留各相机原来的开关状态，不关闭 WebRTC，不停用输入监听、UDP 或场景对象。

## 界面使用说明

控制栏右上角的菱形问号是帮助入口，有两种操作：射线指向问号时显示说明，离开问号立即关闭；点击问号后固定显示，移开射线仍然保留，再次点击问号关闭。说明面板本身不延长悬停，分类页只切换内容，不会自动固定。点击问号固定后即可移入面板切换分类阅读。两只手柄分别记录问号悬停状态，最后一条射线离开问号才关闭临时说明。点击关闭后即使射线还在问号上也不会立即重开，移开再指向可重新预览。

说明分为“手柄按键、界面按钮、布局调整、状态提示、机器人操作”五页，覆盖 A/B、X/Y、Home、双摇杆、扳机点击、UDP/CAM/REC/XYZ、相机独立开关和帧率、拖动与缩放、追踪诊断、急停恢复及重放提示。机器人操作页明确以中间件当前配置为准，并区分摇杆“向外拨”的机器人回零与“向下按”的界面显隐。

说明面板覆盖工具栏并拦截射线，防止点击穿透到下方按钮；顶部急停横幅仍在其上方显示。双摇杆隐藏界面时，说明一同隐藏并清除固定状态。失焦时也收起说明，恢复后可重新打开。查看说明不会暂停 UDP 或录制；需要停止发送时按 B。REC 控件显示状态，实际录制仍用 X/Y。

实现位于 `Assets/InterfaceHelpPanel.cs`，由 `UdpTransmissionButton` 构建工具栏时创建，无需手动修改场景。`Tools/InterfaceHelpRegression.cs` 验证多指针悬停和固定状态；`Tools/InterfaceHelpEditorChecks.cs` 可临时复制到 `Assets/Editor`，调用 `Run(outputDirectory)` 在独立预览场景渲染全部页面，检查文字溢出、实际 UI 事件、点击遮挡与父 CanvasGroup 显隐，不启动应用网络。

帮助校验覆盖编译后程序集的悬停与点击状态，以及编辑器五页预览的文字溢出、问号右上角位置、离开立即关闭、点击固定与再次点击关闭、分类不自动固定、防点击穿透及继承显隐。编辑器检查在 Edit Mode 执行，真机射线体验仍需安装包含本次修改的 APK 后验收。

2026-09-15 调整后验证通过：业务脚本及 Unity 实际导入编译成功，30 项状态断言通过；编辑器五页排版和上述交互检查全部通过。

## 为什么之前 Home 长按无效

旧代码把 `CommonUsages.menuButton` 当作右侧 Home 键轮询。Home 属于系统按键，应用不能靠这个特征可靠识别长按。场景还同时开启了 A、B、menu、单摇杆四种长按监听。

本项目实际使用的 SDK 是 `Packages/manifest.json` 指定的本地 PICO SDK。
`RightControllerLongPressRecenter` 现在订阅：

```csharp
ByteDance.PICO.XR.PXR_Plugin.System.RecenterSuccess
```

该回调可能从原生线程触发，因此仅设置原子标记。Unity 主线程等待应用聚焦、未暂停、头部有效追踪，并留出至少下一帧和默认 0.15 秒的坐标更新窗口，再调用 `InterfaceLayoutResetService.RequestReset`。这个窗口是工程稳定延时，不是 SDK 保证的时序，需在头显上验证。

应用不重新绑定 Home、不主动调用系统复位 API，也不把普通焦点恢复当作复位通知。脚本和原 GUID 保留，现有 `ResetNow`、`ResetInterfacePanels`、`RecenterView` 调用仍有效。

## 与录制标记的冲突

旧中间件把双摇杆 bit 5 组合解释为录制标记。为使本次手势只控制界面，场景默认打开 `consumeThumbstickClicks`：本地保留原始采样，发往中间件的左右 `held/pressed/released` 均清除 bit 5，包括单独按下一侧摇杆；摇杆方向轴和其他按键保留。

若明确希望显隐时仍同时标记录制，可关闭 `RightControllerLongPressRecenter.consumeThumbstickClicks`。`enableBothThumbsticksToggle` 关闭时也不消费点击。无需修改中间件协议或部署中间件代码。

## 验证

1. 在 Unity 打开 `Assets/Scenes/SampleScene.unity` 构建新版 APK。原头显已安装包不会随着源码修改自动更新。
2. 启动后拖动控制面板和相机窗口。转头后长按右 Home，确认系统复位后面板正对当前视线。
3. 短按 Home 打开菜单再返回；不应重置布局。长按 B 只保留停止遥操作效果。
4. 按下两个摇杆一次，确认所有应用面板、射线和坐标轴隐藏；持续按住不应闪烁。
5. 只松开一侧再按回，不应切换。两边都松开再按下，界面应恢复。
6. 关闭一部分相机后重复显隐，原本关闭的相机应仍关闭，原本开启的连接应保持。
7. 隐藏时握住 grip，显示后保持握住，不应开始拖动；先松开重新抓取才能拖动。
8. 界面隐藏时长按 Home，应恢复显示并复位。失焦/丢失手柄追踪期间按住双摇杆，恢复后应先松开再按才切换。
9. 录制期间切换界面，默认不应产生 `recording_marked` 事件。

日志中的 `[Interface] PICO system recenter received` 可区分“系统回调未到达”和“已收到回调但仍等待追踪”的问题。纯输入状态测试见 `Tools/InterfaceShortcutsRegression.cs`；真实 Home 长按事件仍必须用设备确认。

## 本次验证结果（2026-09-15）

- 项目业务脚本独立编译、Unity 编辑器实际导入编译均通过。
- 输入状态及过滤测试通过，覆盖长按不重复、必须双手释放、追踪/焦点丢失，以及全部 65536 种按键掩码。
- 未连接时 A 开启后 B 无法关闭的问题已修正：本地 A/B 不再使用待发送的累计 `pressed` 位，独立保存当前按键边沿，B 具有停止优先级。`Tools/TransmissionButtonsRegression.cs` 的 76 项断言通过，覆盖离线、UI 开启、A+B 同按、保持 A 不自动重启及重复切换；该测试验证编译后的本地状态逻辑，不代替头显实测。
- `Tools/InterfaceVisibilityEditorChecks.cs` 在 Unity 独立空场景的 Edit Mode 中执行，24 项断言通过；原场景已恢复。验证了父子 CanvasGroup 状态、实际继承透明度、动态克隆、保持组件活动和禁止隐藏拖拽；自动 OnDisable 生命周期未在此次 Edit Mode 测试中验证。
- 真机通过 USB 识别为 PICO，已安装 `com.DefaultCompany.HCTeleop` 的更新时间为 2026-09-10。此次未打包或覆盖安装 APK，未把脚本测试当作真实 Home 长按测试。

## v0.1.1 发布构建（2026-09-15）

后续发布步骤已通过 Unity 2022.3.62f3c1 生成 `HC-Teleop-v0.1.1.apk`：

- 应用版本 `0.1.1`，Android versionCode `2`，包名保持 `com.DefaultCompany.HCTeleop`。
- ARM64、最低 Android API 29，正式构建（未启用 Development）。
- APK 签名验证通过，与此前 APK 的签名证书相同，可覆盖升级。
- 在 Unity 菜单 `HC-Teleop > Build Release APK` 可重复构建，输出到 `Builds/`；构建脚本为 `Assets/Editor/HCTeleopReleaseBuild.cs`。
- 发布构建没有自动覆盖头显安装；Home 长按与显隐仍需在安装新版后进行物理按键验收。

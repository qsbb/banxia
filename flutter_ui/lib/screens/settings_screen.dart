import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../core/bridge/bridge_protocol.dart';
import '../main.dart';
import '../scene/pairing_numpad.dart';
import '../state/app_state.dart';

/// Settings tab (design §2.5): root grouped list + detail sub-pages. The
/// Connection page embeds the M3 pairing numpad with six-digit code dots.
class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final AppState app = appState;
    return ListenableBuilder(
      listenable: app,
      builder: (BuildContext context, Widget? _) {
        return SafeArea(
          child: ListView(
            padding: const EdgeInsets.only(bottom: 24),
            children: <Widget>[
              const _NavBar(),
              const _GroupHeader('调试'),
              _Group(children: <Widget>[
                _ToggleRow(
                  label: '调试模式（不拦截报错）',
                  value: app.settings.debugMode,
                  onChanged: (bool v) => app.toggleSetting('debugMode', v),
                ),
                const Padding(
                  padding: EdgeInsets.fromLTRB(16, 0, 16, 12),
                  child: Text(
                    '开启后异常会写入完整堆栈并重新抛出，仅建议改 bug 时使用。',
                    style: TextStyle(
                        fontSize: 13, color: BanxiaTokens.labelSecondary),
                  ),
                ),
              ]),
              const _GroupHeader('连接'),
              _Group(children: <Widget>[
                _NavRow(
                  label: '连接后端',
                  value: app.connection.pairingStatus,
                  onTap: () => _push(context, _ConnectionPage(appState: app)),
                ),
              ]),
              const _GroupHeader('画质'),
              _Group(children: <Widget>[
                _NavRow(
                  label: '渲染画质',
                  value: _presetLabel(app.quality.renderPreset),
                  onTap: () => _push(context, _QualityPage(appState: app)),
                ),
                _NavRow(
                  label: 'MMD 物理',
                  value: _physicsLabel(app.quality.physicsPreset),
                  onTap: () => _push(context, _QualityPage(appState: app)),
                ),
              ]),
              const _GroupHeader('通用'),
              _Group(children: <Widget>[
                _ToggleRow(
                  label: '场景诊断 HUD',
                  value: app.settings.hud,
                  onChanged: (bool v) => app.toggleSetting('hud', v),
                ),
                _ToggleRow(
                  label: '构图网格',
                  value: app.settings.framingGrid,
                  onChanged: (bool v) => app.toggleSetting('framingGrid', v),
                ),
                _ToggleRow(
                  label: '摄像头单帧',
                  value: app.settings.camera,
                  onChanged: (bool v) => app.toggleSetting('camera', v),
                ),
                const Padding(
                  padding: EdgeInsets.fromLTRB(16, 0, 16, 8),
                  child: Text('开启后可在对话中发送当前摄像头画面',
                      style: TextStyle(
                          fontSize: 13, color: BanxiaTokens.labelSecondary)),
                ),
                _NavRow(
                  label: '目标帧率',
                  value: '${app.settings.targetFps} fps',
                  onTap: () => _push(context, _GeneralPage(appState: app)),
                ),
                _NavRow(
                  label: '音量',
                  value: '${(app.settings.volume * 100).round()}%',
                  onTap: () => _push(context, _GeneralPage(appState: app)),
                ),
              ]),
              const _GroupHeader('诊断'),
              _Group(children: <Widget>[
                _NavRow(
                  label: '性能采样',
                  value: _perfLabel(app),
                  onTap: () => _push(context, _PerformancePage(appState: app)),
                ),
                _NavRow(
                  label: '运行日志',
                  value: '${app.diagnostics.logLines.length} 行',
                  onTap: () => _push(context, _LogPage(appState: app)),
                ),
              ]),
              const _GroupHeader('关于'),
              _Group(children: <Widget>[
                _NavRow(
                  label: '检查更新',
                  value: 'v${app.update.version}',
                  onTap: () => _push(context, _UpdatePage(appState: app)),
                ),
                _NavRow(
                  label: '关于伴夏',
                  value: '设备信息',
                  onTap: () => _push(context, _AboutPage(appState: app)),
                ),
              ]),
            ],
          ),
        );
      },
    );
  }

  void _push(BuildContext context, Widget page) {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (BuildContext _) => _SettingsPageScaffold(
          appState: appState,
          child: page,
        ),
      ),
    );
  }

  static String _presetLabel(String v) => switch (v) {
        'performance' => '性能',
        'balanced' => '平衡',
        'clear' => '清晰',
        _ => v,
      };

  static String _physicsLabel(String v) => switch (v) {
        'performance' => '性能',
        'balanced' => '平衡',
        'fine' => '精细',
        _ => v,
      };

  static String _perfLabel(AppState app) {
    final PerfSnapshot? perf = app.diagnostics.perf;
    if (perf == null) return '未采样';
    return 'fps5s=${perf.fps5s.toStringAsFixed(0)} · p50=${perf.frameP50Ms.toStringAsFixed(1)}ms';
  }
}

class _NavBar extends StatelessWidget {
  const _NavBar();

  @override
  Widget build(BuildContext context) {
    return const Padding(
      padding: EdgeInsets.fromLTRB(20, 16, 20, 8),
      child: Align(
        alignment: Alignment.centerLeft,
        child: Text('设置',
            style: TextStyle(
                fontSize: 34,
                fontWeight: FontWeight.bold,
                color: BanxiaTokens.label)),
      ),
    );
  }
}

class _GroupHeader extends StatelessWidget {
  const _GroupHeader(this.text);

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(26, 16, 26, 6),
      child: Text(text.toUpperCase(),
          style: const TextStyle(
              fontSize: 13,
              color: BanxiaTokens.labelSecondary,
              letterSpacing: 0.4)),
    );
  }
}

class _Group extends StatelessWidget {
  const _Group({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.symmetric(horizontal: 20),
      decoration: BoxDecoration(
        color: BanxiaTokens.bgElevated,
        borderRadius: BorderRadius.circular(BanxiaTokens.radiusGroup),
      ),
      child: Column(
        children: <Widget>[
          for (int i = 0; i < children.length; i++) ...<Widget>[
            if (i > 0)
              const Divider(
                  height: 1,
                  indent: 16,
                  endIndent: 16,
                  color: BanxiaTokens.separator),
            children[i],
          ],
        ],
      ),
    );
  }
}

class _NavRow extends StatelessWidget {
  const _NavRow(
      {required this.label, required this.value, required this.onTap});

  final String label;
  final String value;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        child: Row(
          children: <Widget>[
            Expanded(
              child: Text(label,
                  style:
                      const TextStyle(fontSize: 16, color: BanxiaTokens.label)),
            ),
            Text(value,
                style: const TextStyle(
                    fontSize: 16, color: BanxiaTokens.labelSecondary)),
            const SizedBox(width: 6),
            const Icon(Icons.chevron_right,
                color: BanxiaTokens.labelTertiary, size: 22),
          ],
        ),
      ),
    );
  }
}

class _ToggleRow extends StatelessWidget {
  const _ToggleRow({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Text(label,
                style:
                    const TextStyle(fontSize: 16, color: BanxiaTokens.label)),
          ),
          Switch(value: value, onChanged: onChanged),
        ],
      ),
    );
  }
}

/// Scaffold wrapper for detail pages (iOS `< 设置` back + title).
class _SettingsPageScaffold extends StatelessWidget {
  const _SettingsPageScaffold({required this.appState, required this.child});

  final AppState appState;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: BanxiaTokens.bg,
      appBar: AppBar(
        backgroundColor: BanxiaTokens.bg,
        elevation: 0,
        leading: BackButton(color: BanxiaTokens.tint),
      ),
      body: child,
    );
  }
}

// ── Connection page (M3 pairing) ────────────────────────────────────────────
class _ConnectionPage extends StatefulWidget {
  const _ConnectionPage({required this.appState});

  final AppState appState;

  @override
  State<_ConnectionPage> createState() => _ConnectionPageState();
}

class _ConnectionPageState extends State<_ConnectionPage> {
  late bool _numpadExpanded;
  bool _numpadTouched = false;

  @override
  void initState() {
    super.initState();
    _numpadExpanded = widget.appState.connection.serverDraft.trim().isEmpty;
  }

  @override
  Widget build(BuildContext context) {
    final AppState app = widget.appState;
    return ListenableBuilder(
      listenable: app,
      builder: (BuildContext context, Widget? _) {
        final double screenW = MediaQuery.sizeOf(context).width;
        final double screenH = MediaQuery.sizeOf(context).height;
        final bool numpadExpanded = _numpadTouched
            ? _numpadExpanded
            : app.connection.serverDraft.trim().isEmpty;
        return ListView(
          padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
          children: <Widget>[
            const _PageTitle('连接后端'),
            _ServerField(appState: app),
            const SizedBox(height: 8),
            _SwitchRow(
              // 该开关同时是公网明文 HTTP 的本地 opt-in：开启后允许配对/入口
              // 使用 http:// 公网地址，密钥与语音将明文传输，仅限自有服务器。
              label: '允许明文 HTTP（内网/公网直连，公网明文有泄漏风险）',
              value: app.connection.privateHttp,
              onChanged: (bool v) => app.dispatch(
                  Cmd.pairingSetPrivateHttp, <String, dynamic>{'enabled': v}),
            ),
            const SizedBox(height: 16),
            _EndpointSection(appState: app),
            const SizedBox(height: 8),
            InkWell(
              onTap: () => setState(() {
                _numpadTouched = true;
                _numpadExpanded = !numpadExpanded;
              }),
              child: Padding(
                padding:
                    const EdgeInsets.symmetric(horizontal: 8, vertical: 12),
                child: Row(
                  children: <Widget>[
                    const Expanded(
                      child: Text('配对码',
                          style: TextStyle(
                              fontSize: 16, color: BanxiaTokens.label)),
                    ),
                    Text(
                      app.connection.pairingCode.isEmpty
                          ? '输入 6 位'
                          : '${app.connection.pairingCode.length}/6',
                      style: const TextStyle(
                          fontSize: 14, color: BanxiaTokens.labelSecondary),
                    ),
                    const SizedBox(width: 6),
                    Icon(
                      numpadExpanded ? Icons.expand_less : Icons.expand_more,
                      color: BanxiaTokens.labelTertiary,
                    ),
                  ],
                ),
              ),
            ),
            if (numpadExpanded) ...<Widget>[
              _CodeDots(codeLength: app.connection.pairingCode.length),
              const SizedBox(height: 4),
              Center(
                child: PairingNumpad(
                  availableHeight: screenH,
                  availableWidth: screenW - 40,
                  onDigit: app.appendPairingDigit,
                  onBackspace: app.removePairingDigit,
                  onClear: app.clearPairingCode,
                  onSubmit: app.submitPairing,
                ),
              ),
              const SizedBox(height: 16),
            ],
            _PrimaryButton(label: '连接后端', onTap: app.submitPairing),
            const SizedBox(height: 8),
            Row(
              children: <Widget>[
                Expanded(
                  child: _GlassButton(
                    label: '重新连接',
                    onTap: () => app.dispatch(Cmd.pairingReconnect),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: _GlassButton(
                    label: '解除绑定',
                    onTap: () => app.dispatch(Cmd.pairingClearBinding),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 16),
            Text(
              '连接状态：${app.connection.pairingStatus}',
              textAlign: TextAlign.center,
              style: const TextStyle(
                  fontSize: 13, color: BanxiaTokens.labelSecondary),
            ),
          ],
        );
      },
    );
  }
}

class _ServerField extends StatefulWidget {
  const _ServerField({required this.appState});

  final AppState appState;

  @override
  State<_ServerField> createState() => _ServerFieldState();
}

class _ServerFieldState extends State<_ServerField> {
  late final TextEditingController _controller =
      TextEditingController(text: widget.appState.connection.serverDraft);
  final FocusNode _focusNode = FocusNode();

  @override
  void dispose() {
    _controller.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (!_focusNode.hasFocus &&
        _controller.text != widget.appState.connection.serverDraft) {
      _controller.text = widget.appState.connection.serverDraft;
    }
    return Container(
      height: 48,
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        color: BanxiaTokens.glass,
        borderRadius: BorderRadius.circular(12),
      ),
      child: TextField(
        controller: _controller,
        focusNode: _focusNode,
        maxLength: 512,
        maxLengthEnforcement: MaxLengthEnforcement.enforced,
        style: const TextStyle(fontSize: 16, color: BanxiaTokens.label),
        decoration: const InputDecoration(
          border: InputBorder.none,
          // 引擎对裸输入默认补 http:// 前缀；公网明文 HTTP 在明文开关
          // （默认开）下放行。只有服务器确实配了 TLS 证书时才用 https://，
          // 否则会报 "Unable to complete SSL connection"。
          hintText: '填 域名:端口 或 IP:端口（默认明文 http）',
          hintStyle:
              TextStyle(fontSize: 16, color: BanxiaTokens.labelSecondary),
          counterText: '',
        ),
        onChanged: (String v) {
          widget.appState.updatePairingServerDraft(v);
        },
        onSubmitted: (String v) async {
          await widget.appState.commitPairingServer(v);
          if (mounted) {
            _controller.text = widget.appState.connection.serverDraft;
          }
        },
      ),
    );
  }
}

class _CodeDots extends StatelessWidget {
  const _CodeDots({required this.codeLength});

  final int codeLength;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: <Widget>[
        for (int i = 0; i < 6; i++)
          Container(
            width: 14,
            height: 14,
            margin: const EdgeInsets.symmetric(horizontal: 8),
            decoration: BoxDecoration(
              color:
                  i < codeLength ? BanxiaTokens.label : const Color(0x66787880),
              shape: BoxShape.circle,
            ),
          ),
      ],
    );
  }
}

class _SwitchRow extends StatelessWidget {
  const _SwitchRow({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        color: BanxiaTokens.bgCard,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Text(label,
                style:
                    const TextStyle(fontSize: 16, color: BanxiaTokens.label)),
          ),
          Switch(value: value, onChanged: onChanged),
        ],
      ),
    );
  }
}

// ── 入口优先级列表（pairing.endpoint*）──────────────────────────────────────
/// 有序展示候选后端入口：首项=最高优先级；当前实际承载流量的入口（==
/// activeEndpoint）加「生效中」标记。删除/排序一律把完整 URL 原文回传引擎，
/// 列表显示时缩成 host:port。故障转移语义：仅网络不可达才顺延下一候选，
/// 鉴权失败不转移；配对凭据对所有入口通用，换入口无需重新配对。
class _EndpointSection extends StatefulWidget {
  const _EndpointSection({required this.appState});

  final AppState appState;

  @override
  State<_EndpointSection> createState() => _EndpointSectionState();
}

class _EndpointSectionState extends State<_EndpointSection> {
  final TextEditingController _addController = TextEditingController();
  final FocusNode _addFocus = FocusNode();

  @override
  void dispose() {
    _addController.dispose();
    _addFocus.dispose();
    super.dispose();
  }

  /// 把完整 URL 缩成 host:port 显示；解析失败时原样返回。
  static String _shortEndpoint(String url) {
    final Uri? uri = Uri.tryParse(url);
    if (uri == null || uri.host.isEmpty) return url;
    return uri.hasPort ? '${uri.host}:${uri.port}' : uri.host;
  }

  Future<void> _submitAdd() async {
    final AppState app = widget.appState;
    final bool ok = await app.addEndpoint(_addController.text);
    // 仅引擎接受后清空输入框，失败时保留用户输入便于修改。
    if (ok && mounted) _addController.clear();
  }

  @override
  Widget build(BuildContext context) {
    final AppState app = widget.appState;
    final List<String> endpoints = app.connection.endpoints;
    final String active = app.connection.activeEndpoint;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        const Padding(
          padding: EdgeInsets.fromLTRB(6, 0, 6, 6),
          child: Text('入口优先级',
              style: TextStyle(fontSize: 16, color: BanxiaTokens.label)),
        ),
        Container(
          width: double.infinity,
          decoration: BoxDecoration(
            color: BanxiaTokens.bgCard,
            borderRadius: BorderRadius.circular(12),
          ),
          child: endpoints.isEmpty
              ? const Padding(
                  padding: EdgeInsets.all(16),
                  child: Text(
                    '配对后自动生成首个入口；可添加内网/公网/兜底入口，排最上的优先使用',
                    style: TextStyle(
                        fontSize: 13, color: BanxiaTokens.labelSecondary),
                  ),
                )
              : Column(
                  children: <Widget>[
                    for (int i = 0; i < endpoints.length; i++) ...<Widget>[
                      if (i > 0)
                        const Divider(
                            height: 1,
                            indent: 16,
                            endIndent: 16,
                            color: BanxiaTokens.separator),
                      _EndpointRow(
                        index: i,
                        total: endpoints.length,
                        display: _shortEndpoint(endpoints[i]),
                        isActive: endpoints[i] == active,
                        isTesting: app.isEndpointTesting(endpoints[i]),
                        testResult: app.endpointTestResult(endpoints[i]),
                        onTest: () => app.testEndpoint(endpoints[i]),
                        onMoveUp: () => app.moveEndpoint(endpoints[i], -1),
                        onMoveDown: () => app.moveEndpoint(endpoints[i], 1),
                        onRemove: () => app.removeEndpoint(endpoints[i]),
                      ),
                    ],
                  ],
                ),
        ),
        const SizedBox(height: 8),
        Row(
          children: <Widget>[
            Expanded(
              child: Container(
                height: 44,
                padding: const EdgeInsets.symmetric(horizontal: 16),
                decoration: BoxDecoration(
                  color: BanxiaTokens.glass,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: TextField(
                  controller: _addController,
                  focusNode: _addFocus,
                  maxLength: 512,
                  maxLengthEnforcement: MaxLengthEnforcement.enforced,
                  style:
                      const TextStyle(fontSize: 14, color: BanxiaTokens.label),
                  decoration: const InputDecoration(
                    border: InputBorder.none,
                    hintText: '填 域名:端口 或 IP:端口（默认明文 http）',
                    hintStyle: TextStyle(
                        fontSize: 14, color: BanxiaTokens.labelSecondary),
                    counterText: '',
                  ),
                  onSubmitted: (_) => _submitAdd(),
                ),
              ),
            ),
            const SizedBox(width: 10),
            GestureDetector(
              onTap: _submitAdd,
              child: Container(
                height: 44,
                padding: const EdgeInsets.symmetric(horizontal: 20),
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: BanxiaTokens.tintFill,
                  borderRadius: BorderRadius.circular(22),
                ),
                child: const Text('添加',
                    style: TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.bold,
                        color: Colors.white)),
              ),
            ),
          ],
        ),
      ],
    );
  }
}

/// 入口列表单行：序号 + 简化地址（+「生效中」标记）+ 上移/下移/删除。
/// 首项禁用上移、末项禁用下移；删除与排序回传完整 URL 原文。
class _EndpointRow extends StatelessWidget {
  const _EndpointRow({
    required this.index,
    required this.total,
    required this.display,
    required this.isActive,
    required this.isTesting,
    required this.testResult,
    required this.onTest,
    required this.onMoveUp,
    required this.onMoveDown,
    required this.onRemove,
  });

  final int index;
  final int total;
  final String display;
  final bool isActive;
  final bool isTesting;
  final EndpointTestResult? testResult;
  final VoidCallback onTest;
  final VoidCallback onMoveUp;
  final VoidCallback onMoveDown;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    final bool canUp = index > 0;
    final bool canDown = index < total - 1;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 10, 8, 10),
      child: Row(
        children: <Widget>[
          Text('${index + 1}',
              style: const TextStyle(
                  fontSize: 14, color: BanxiaTokens.labelTertiary)),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Text(display,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                        fontSize: 15, color: BanxiaTokens.label)),
                if (isTesting)
                  const Padding(
                    padding: EdgeInsets.only(top: 2),
                    child: Text('测试中…',
                        style: TextStyle(
                            fontSize: 12, color: BanxiaTokens.labelSecondary)),
                  ),
                if (!isTesting && testResult != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 2),
                    child: Text(
                      _testResultText(testResult!),
                      style: TextStyle(
                        fontSize: 12,
                        color: testResult!.ok
                            ? BanxiaTokens.tint
                            : BanxiaTokens.labelSecondary,
                      ),
                    ),
                  ),
                if (isActive)
                  const Padding(
                    padding: EdgeInsets.only(top: 2),
                    child: Text('生效中',
                        style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.bold,
                            color: BanxiaTokens.tint)),
                  ),
              ],
            ),
          ),
          Tooltip(
            message: '测试入口连通性',
            child: _EndpointAction(
              icon: Icons.network_check,
              enabled: !isTesting,
              onTap: onTest,
            ),
          ),
          _EndpointAction(
            icon: Icons.arrow_upward,
            enabled: canUp,
            onTap: onMoveUp,
          ),
          _EndpointAction(
            icon: Icons.arrow_downward,
            enabled: canDown,
            onTap: onMoveDown,
          ),
          _EndpointAction(
            icon: Icons.close,
            enabled: true,
            onTap: onRemove,
          ),
        ],
      ),
    );
  }
}

String _testResultText(EndpointTestResult result) {
  if (result.ok) return '✓ ${result.httpCode} · ${result.elapsedMs}ms';
  final String label = switch (result.errorKind) {
    'timeout' => '超时',
    'ssl' => 'SSL',
    'dns' => '解析失败',
    'refused' => '拒绝',
    'http' => 'HTTP ${result.httpCode}',
    'protocol' => '协议不兼容',
    'response_too_large' => '响应过大',
    _ => '连接失败',
  };
  return '✗ $label';
}

class _EndpointAction extends StatelessWidget {
  const _EndpointAction({
    required this.icon,
    required this.enabled,
    required this.onTap,
  });

  final IconData icon;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: enabled ? onTap : null,
      child: Padding(
        padding: const EdgeInsets.all(8),
        child: Icon(
          icon,
          size: 20,
          color: enabled
              ? BanxiaTokens.labelSecondary
              : BanxiaTokens.labelTertiary,
        ),
      ),
    );
  }
}

// ── Quality page ────────────────────────────────────────────────────────────
class _QualityPage extends StatelessWidget {
  const _QualityPage({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final AppState app = appState;
    return ListenableBuilder(
      listenable: app,
      builder: (BuildContext context, Widget? _) {
        return ListView(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
          children: <Widget>[
            const _PageTitle('渲染画质'),
            _Segmented<String>(
              value: app.quality.renderPreset,
              options: const <(String, String)>[
                ('performance', '性能'),
                ('balanced', '平衡'),
                ('clear', '清晰'),
              ],
              onChanged: (String v) => app.dispatch(
                  Cmd.qualityApplyPreset, <String, dynamic>{'preset': v}),
            ),
            const SizedBox(height: 20),
            const _PageTitle('MMD 物理'),
            _Segmented<String>(
              value: app.quality.physicsPreset,
              options: const <(String, String)>[
                ('performance', '性能'),
                ('balanced', '平衡'),
                ('fine', '精细'),
              ],
              onChanged: (String v) => app.dispatch(
                  Cmd.qualityApplyPhysics, <String, dynamic>{'preset': v}),
            ),
            const SizedBox(height: 20),
            _GlassButton(
              label: '恢复默认画质',
              onTap: () => app.dispatch(Cmd.qualityReset),
            ),
            if (app.quality.status.isNotEmpty) ...<Widget>[
              const SizedBox(height: 12),
              Text(app.quality.status,
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                      fontSize: 13, color: BanxiaTokens.labelSecondary)),
            ],
          ],
        );
      },
    );
  }
}

// ── Performance page ────────────────────────────────────────────────────────
class _PerformancePage extends StatelessWidget {
  const _PerformancePage({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final AppState app = appState;
    return ListenableBuilder(
      listenable: app,
      builder: (BuildContext context, Widget? _) {
        final PerfSnapshot? perf = app.diagnostics.perf;
        return ListView(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
          children: <Widget>[
            const _PageTitle('性能采样'),
            _InfoRow(
                'fps (5s)', perf == null ? '—' : perf.fps5s.toStringAsFixed(1)),
            _InfoRow('fps (30s)',
                perf == null ? '—' : perf.fps30s.toStringAsFixed(1)),
            _InfoRow(
                '帧 p50',
                perf == null
                    ? '—'
                    : '${perf.frameP50Ms.toStringAsFixed(1)} ms'),
            _InfoRow(
                '帧 p95',
                perf == null
                    ? '—'
                    : '${perf.frameP95Ms.toStringAsFixed(1)} ms'),
            _InfoRow('pose_src_flip', '${app.diagnostics.poseSrcFlip}'),
          ],
        );
      },
    );
  }
}

// ── Log page ────────────────────────────────────────────────────────────────
class _LogPage extends StatelessWidget {
  const _LogPage({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final AppState app = appState;
    return ListenableBuilder(
      listenable: app,
      builder: (BuildContext context, Widget? _) {
        return Column(
          children: <Widget>[
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
              child: Row(
                children: <Widget>[
                  Expanded(
                    child: _GlassButton(
                      label: '刷新日志',
                      onTap: () => app.dispatch(Cmd.logRefresh),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: _GlassButton(
                      label: '清空日志',
                      onTap: () => app.dispatch(Cmd.logClear),
                    ),
                  ),
                ],
              ),
            ),
            Expanded(
              child: ListView(
                padding: const EdgeInsets.symmetric(horizontal: 20),
                children: <Widget>[
                  for (final String line in app.diagnostics.logLines)
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 2),
                      child: Text(line,
                          style: const TextStyle(
                              fontSize: 13,
                              color: BanxiaTokens.labelSecondary,
                              fontFamily: 'monospace')),
                    ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
}

// ── General / Update / About pages ──────────────────────────────────────────
class _GeneralPage extends StatelessWidget {
  const _GeneralPage({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: appState,
      builder: (context, _) => ListView(
        padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
        children: <Widget>[
          const _PageTitle('通用'),
          const Text('目标帧率',
              style: TextStyle(fontSize: 16, color: BanxiaTokens.label)),
          const SizedBox(height: 8),
          _Segmented<int>(
            value: appState.settings.targetFps,
            options: const <(int, String)>[
              (30, '30'),
              (60, '60'),
              (120, '120')
            ],
            onChanged: appState.setTargetFps,
          ),
          const SizedBox(height: 22),
          Row(children: <Widget>[
            const Expanded(
                child: Text('音量',
                    style: TextStyle(fontSize: 16, color: BanxiaTokens.label))),
            Text('${(appState.settings.volume * 100).round()}%',
                style: const TextStyle(color: BanxiaTokens.labelSecondary)),
          ]),
          Slider(
            value: appState.settings.volume,
            onChanged: (v) => appState.setVolume(v),
          ),
          _ToggleRow(
              label: '摄像头单帧',
              value: appState.settings.camera,
              onChanged: (v) => appState.toggleSetting('camera', v)),
          const Text('摄像头单帧只在发送对话时采集一张当前画面，不会持续录制。',
              style:
                  TextStyle(fontSize: 13, color: BanxiaTokens.labelSecondary)),
        ],
      ),
    );
  }
}

class _UpdatePage extends StatelessWidget {
  const _UpdatePage({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final AppState app = appState;
    return ListenableBuilder(
      listenable: app,
      builder: (BuildContext context, Widget? _) {
        return ListView(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
          children: <Widget>[
            const _PageTitle('检查更新'),
            _InfoRow('当前版本', 'v${app.update.version}'),
            _InfoRow('阶段', app.update.phase),
            const SizedBox(height: 12),
            LinearProgressIndicator(
              value: app.update.progress <= 0 ? null : app.update.progress,
              color: BanxiaTokens.tint,
            ),
            const SizedBox(height: 16),
            _PrimaryButton(
              label: '检查更新',
              onTap: () => app.dispatch(Cmd.updateCheck),
            ),
            if (app.update.hasUpdate) ...<Widget>[
              const SizedBox(height: 10),
              _PrimaryButton(
                label: '安装 v${app.update.version}',
                onTap: () => app.dispatch(Cmd.updateInstall),
              ),
            ],
          ],
        );
      },
    );
  }
}

class _AboutPage extends StatelessWidget {
  const _AboutPage({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
      children: <Widget>[
        const _PageTitle('关于伴夏'),
        _InfoRow('版本', 'v${appState.update.version}'),
        const _InfoRow('设备', '本地样本设备'),
        const _InfoRow('内存', '—'),
      ],
    );
  }
}

// ── Shared small widgets ────────────────────────────────────────────────────
class _PageTitle extends StatelessWidget {
  const _PageTitle(this.text);

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Text(text,
          style: const TextStyle(
              fontSize: 28,
              fontWeight: FontWeight.bold,
              color: BanxiaTokens.label)),
    );
  }
}

class _InfoRow extends StatelessWidget {
  const _InfoRow(this.label, this.value);

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 10),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Text(label,
                style:
                    const TextStyle(fontSize: 16, color: BanxiaTokens.label)),
          ),
          Text(value,
              style: const TextStyle(
                  fontSize: 16, color: BanxiaTokens.labelSecondary)),
        ],
      ),
    );
  }
}

class _Segmented<T> extends StatelessWidget {
  const _Segmented({
    required this.value,
    required this.options,
    required this.onChanged,
  });

  final T value;
  final List<(T, String)> options;
  final ValueChanged<T> onChanged;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(4),
      decoration: BoxDecoration(
        color: BanxiaTokens.glass,
        borderRadius: BorderRadius.circular(14),
      ),
      child: Row(
        children: <Widget>[
          for (final (T optionValue, String label) in options)
            Expanded(
              child: GestureDetector(
                onTap: () => onChanged(optionValue),
                child: Container(
                  height: 38,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: optionValue == value
                        ? Colors.white
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(11),
                  ),
                  child: Text(
                    label,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: optionValue == value
                          ? FontWeight.bold
                          : FontWeight.normal,
                      color: BanxiaTokens.label,
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _PrimaryButton extends StatelessWidget {
  const _PrimaryButton({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        height: 50,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: BanxiaTokens.tintFill,
          borderRadius: BorderRadius.circular(25),
        ),
        child: Text(label,
            style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.bold,
                color: Colors.white)),
      ),
    );
  }
}

class _GlassButton extends StatelessWidget {
  const _GlassButton({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        height: 48,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: BanxiaTokens.glass,
          borderRadius: BorderRadius.circular(24),
          border: Border.all(color: const Color(0x1F000000), width: 1),
        ),
        child: Text(label,
            style: const TextStyle(fontSize: 15, color: BanxiaTokens.label)),
      ),
    );
  }
}

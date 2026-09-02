import 'package:flutter/material.dart';

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
class _ConnectionPage extends StatelessWidget {
  const _ConnectionPage({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final AppState app = appState;
    return ListenableBuilder(
      listenable: app,
      builder: (BuildContext context, Widget? _) {
        final double screenW = MediaQuery.sizeOf(context).width;
        final double screenH = MediaQuery.sizeOf(context).height;
        return ListView(
          padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
          children: <Widget>[
            const _PageTitle('连接后端'),
            _ServerField(appState: app),
            const SizedBox(height: 8),
            _SwitchRow(
              label: '私网 HTTP',
              value: app.connection.privateHttp,
              onChanged: (bool v) => app.dispatch(
                  Cmd.pairingSetPrivateHttp, <String, dynamic>{'enabled': v}),
            ),
            const SizedBox(height: 16),
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
      TextEditingController(text: widget.appState.connection.server);

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 48,
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        color: BanxiaTokens.glass,
        borderRadius: BorderRadius.circular(12),
      ),
      child: TextField(
        controller: _controller,
        style: const TextStyle(fontSize: 16, color: BanxiaTokens.label),
        decoration: const InputDecoration(
          border: InputBorder.none,
          hintText: '服务器域名 / IP:端口',
          hintStyle:
              TextStyle(fontSize: 16, color: BanxiaTokens.labelSecondary),
        ),
        onSubmitted: (String v) => widget.appState
            .dispatch(Cmd.pairingSetServer, <String, dynamic>{'server': v}),
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

import 'package:flutter/material.dart';

import '../core/bridge/bridge_protocol.dart';
import '../main.dart';
import '../state/app_state.dart';

/// Actions tab (design §2.4): idle preset row, expression cycle, refresh/
/// import, and the VMD action-card list. "Playing" badge = `playingId == id`.
class ActionsScreen extends StatelessWidget {
  const ActionsScreen({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: appState,
      builder: (BuildContext context, Widget? _) {
        return SafeArea(
          child: Column(
            children: <Widget>[
              const _NavBar(),
              _ControlRow(appState: appState),
              Expanded(
                child: ListView(
                  padding: const EdgeInsets.symmetric(horizontal: 20),
                  children: <Widget>[
                    for (final VmdActionInfo action in appState.actions.actions)
                      _ActionCard(appState: appState, action: action),
                    const SizedBox(height: 20),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _NavBar extends StatelessWidget {
  const _NavBar();

  @override
  Widget build(BuildContext context) {
    return const Padding(
      padding: EdgeInsets.fromLTRB(20, 16, 20, 4),
      child: Align(
        alignment: Alignment.centerLeft,
        child: Text('动作',
            style: TextStyle(
                fontSize: 34,
                fontWeight: FontWeight.bold,
                color: BanxiaTokens.label)),
      ),
    );
  }
}

class _ControlRow extends StatelessWidget {
  const _ControlRow({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 8, 20, 8),
      child: Column(
        children: <Widget>[
          Row(
            children: <Widget>[
              Expanded(
                child: _Pill(
                  label: '待机：${appState.actions.idlePreset}',
                  onTap: () => appState.dispatch(Cmd.idleCycle),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _Pill(
                  label: '表情：${appState.actions.expression}',
                  onTap: () => appState.dispatch(Cmd.expressionCycle),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Row(
            children: <Widget>[
              Expanded(
                child: _Pill(
                  label: '停止动作',
                  onTap: () => appState.dispatch(Cmd.actionStop),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _Pill(
                  label: '刷新动作',
                  onTap: () => appState.dispatch(Cmd.actionRefresh),
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: _Pill(
                  label: '导入 VMD',
                  onTap: () => appState.dispatch(Cmd.modelImport),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _Pill extends StatelessWidget {
  const _Pill({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        height: 42,
        alignment: Alignment.center,
        padding: const EdgeInsets.symmetric(horizontal: 12),
        decoration: BoxDecoration(
          color: BanxiaTokens.glass,
          borderRadius: BorderRadius.circular(21),
        ),
        child: Text(label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 14, color: BanxiaTokens.label)),
      ),
    );
  }
}

class _ActionCard extends StatelessWidget {
  const _ActionCard({required this.appState, required this.action});

  final AppState appState;
  final VmdActionInfo action;

  @override
  Widget build(BuildContext context) {
    final bool playing = appState.actions.playingId == action.id;
    return Container(
      margin: const EdgeInsets.only(bottom: 10),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: BanxiaTokens.bgCard,
        borderRadius: BorderRadius.circular(BanxiaTokens.radiusCard),
      ),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                Row(
                  children: <Widget>[
                    Text(action.name,
                        style: const TextStyle(
                            fontWeight: FontWeight.bold,
                            fontSize: 17,
                            color: BanxiaTokens.label)),
                    if (playing) ...<Widget>[
                      const SizedBox(width: 8),
                      Container(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 8, vertical: 2),
                        decoration: BoxDecoration(
                          color: BanxiaTokens.green.withOpacity(0.15),
                          borderRadius: BorderRadius.circular(999),
                        ),
                        child: const Text('播放中',
                            style: TextStyle(
                                fontSize: 12, color: BanxiaTokens.green)),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  _metaText(),
                  style: const TextStyle(
                      fontSize: 13, color: BanxiaTokens.labelSecondary),
                ),
              ],
            ),
          ),
          _ActionButton(
            label: playing ? '停止' : '播放',
            primary: true,
            onTap: () => appState
                .dispatch(Cmd.actionPlay, <String, dynamic>{'id': action.id}),
          ),
          const SizedBox(width: 8),
          _ActionButton(
            label: '删除',
            onTap: () => appState
                .dispatch(Cmd.actionDelete, <String, dynamic>{'id': action.id}),
          ),
        ],
      ),
    );
  }

  String _metaText() {
    final List<String> parts = <String>[
      if (action.duration.isNotEmpty && action.duration != '—')
        '时长 ${action.duration}',
      if (action.frames > 0) '${action.frames} 帧',
      if (action.hasExpression) '含表情',
    ];
    return parts.join(' · ');
  }
}

class _ActionButton extends StatelessWidget {
  const _ActionButton({
    required this.label,
    required this.onTap,
    this.primary = false,
  });

  final String label;
  final VoidCallback onTap;
  final bool primary;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        decoration: BoxDecoration(
          color: primary ? BanxiaTokens.tintFill : BanxiaTokens.glass,
          borderRadius: BorderRadius.circular(999),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontSize: 14,
            fontWeight: FontWeight.bold,
            color: primary ? Colors.white : BanxiaTokens.labelSecondary,
          ),
        ),
      ),
    );
  }
}

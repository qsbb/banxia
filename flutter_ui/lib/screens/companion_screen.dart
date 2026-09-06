import 'package:flutter/material.dart';

import '../core/bridge/bridge_protocol.dart';
import '../main.dart';
import '../state/app_state.dart';

/// Companion / home tab (design §2.2): model library, import/refresh, and
/// quick-entry tiles for the other surfaces.
class CompanionScreen extends StatelessWidget {
  const CompanionScreen({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: appState,
      builder: (BuildContext context, Widget? _) {
        return SafeArea(
          child: CustomScrollView(
            slivers: <Widget>[
              const SliverToBoxAdapter(
                  child: _NavBar(title: '伴夏', subtitle: '模型与入口')),
              SliverPadding(
                padding: const EdgeInsets.symmetric(horizontal: 20),
                sliver: SliverList(
                  delegate: SliverChildListDelegate(<Widget>[
                    _ImportRow(appState: appState),
                    const SizedBox(height: 8),
                    ..._modelCards(),
                    if (appState.models.models.isEmpty) const _EmptyHint(),
                  ]),
                ),
              ),
              SliverToBoxAdapter(child: _QuickTiles(appState: appState)),
              const SliverToBoxAdapter(child: SizedBox(height: 24)),
            ],
          ),
        );
      },
    );
  }

  List<Widget> _modelCards() {
    return appState.models.models
        .map((ModelInfo m) => _ModelCard(appState: appState, model: m))
        .toList();
  }
}

class _NavBar extends StatelessWidget {
  const _NavBar({required this.title, required this.subtitle});

  final String title;
  final String subtitle;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Text(
            title,
            style: const TextStyle(
                fontSize: 34,
                fontWeight: FontWeight.bold,
                color: BanxiaTokens.label),
          ),
          Text(
            subtitle,
            style: const TextStyle(
                fontSize: 15, color: BanxiaTokens.labelSecondary),
          ),
        ],
      ),
    );
  }
}

class _ImportRow extends StatelessWidget {
  const _ImportRow({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: <Widget>[
        Expanded(
          child: _GlassButton(
            label: '导入模型 / 动作文件',
            onTap: () => appState.dispatch(Cmd.modelImport),
          ),
        ),
        const SizedBox(width: 10),
        _GlassButton(
          label: '刷新',
          onTap: () => appState.dispatch(Cmd.modelDiscover),
        ),
      ],
    );
  }
}

class _ModelCard extends StatelessWidget {
  const _ModelCard({required this.appState, required this.model});

  final AppState appState;
  final ModelInfo model;

  @override
  Widget build(BuildContext context) {
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
                    Text(
                      model.displayName,
                      style: const TextStyle(
                          fontWeight: FontWeight.bold,
                          fontSize: 17,
                          color: BanxiaTokens.label),
                    ),
                    if (model.inUse) ...<Widget>[
                      const SizedBox(width: 8),
                      const _Badge(text: '使用中'),
                    ],
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  model.size,
                  style: const TextStyle(
                      fontSize: 13, color: BanxiaTokens.labelSecondary),
                ),
              ],
            ),
          ),
          _PillButton(
            label: model.inUse ? '进入' : '查看',
            primary: true,
            onTap: () => appState.enterScene(model.path),
          ),
          const SizedBox(width: 8),
          _PillButton(
            label: '删除',
            onTap: () => _confirmDelete(context),
          ),
        ],
      ),
    );
  }

  void _confirmDelete(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (BuildContext ctx) => AlertDialog(
        title: const Text('删除模型'),
        content: Text('确定删除「${model.displayName}」吗？'),
        actions: <Widget>[
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('取消'),
          ),
          TextButton(
            onPressed: () {
              Navigator.of(ctx).pop();
              appState.dispatch(
                  Cmd.modelDelete, <String, dynamic>{'path': model.path});
            },
            child: const Text('删除', style: TextStyle(color: BanxiaTokens.red)),
          ),
        ],
      ),
    );
  }
}

class _Badge extends StatelessWidget {
  const _Badge({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: BanxiaTokens.green.withOpacity(0.15),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(text,
          style: const TextStyle(fontSize: 12, color: BanxiaTokens.green)),
    );
  }
}

class _EmptyHint extends StatelessWidget {
  const _EmptyHint();

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(vertical: 40, horizontal: 24),
      alignment: Alignment.center,
      child: const Text(
        '还没有模型\n点上方「导入模型 / 动作文件」添加',
        textAlign: TextAlign.center,
        style: TextStyle(fontSize: 15, color: BanxiaTokens.labelSecondary),
      ),
    );
  }
}

class _QuickTiles extends StatelessWidget {
  const _QuickTiles({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final AppState app = appState;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          const Text('快捷入口',
              style:
                  TextStyle(fontSize: 13, color: BanxiaTokens.labelSecondary)),
          const SizedBox(height: 8),
          Row(
            children: <Widget>[
              Expanded(
                  child: _Tile(
                      label: '对话',
                      sub: '文字与语音',
                      onTap: () => app.switchTab(AppTab.chat))),
              const SizedBox(width: 10),
              Expanded(
                  child: _Tile(
                      label: '动作',
                      sub: 'VMD 库',
                      onTap: () => app.switchTab(AppTab.actions))),
            ],
          ),
          const SizedBox(height: 10),
          Row(
            children: <Widget>[
              Expanded(
                  child: _Tile(
                      label: '设置',
                      sub: 'Debug · 帧率 · 连接',
                      onTap: () => app.switchTab(AppTab.settings))),
              const SizedBox(width: 10),
              Expanded(
                  child: _Tile(
                      label: '更新',
                      sub: '检查新版本',
                      onTap: () => app.switchTab(AppTab.settings))),
            ],
          ),
        ],
      ),
    );
  }
}

class _Tile extends StatelessWidget {
  const _Tile({required this.label, required this.sub, required this.onTap});

  final String label;
  final String sub;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        height: 88,
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: BanxiaTokens.glass,
          borderRadius: BorderRadius.circular(16),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.end,
          children: <Widget>[
            Text(label,
                style: const TextStyle(
                    fontWeight: FontWeight.bold,
                    fontSize: 17,
                    color: BanxiaTokens.label)),
            Text(sub,
                style: const TextStyle(
                    fontSize: 12, color: BanxiaTokens.labelSecondary)),
          ],
        ),
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

class _PillButton extends StatelessWidget {
  const _PillButton({
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

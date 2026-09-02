import 'package:flutter/material.dart';

import '../core/bridge/bridge_protocol.dart';
import '../main.dart';
import '../state/app_state.dart';

/// Chat tab (design §2.3): connection badge, guided card when disconnected,
/// status card + bubble list (24-cap) + quick phrases + input bar when
/// connected. The disconnected/connected branch is derived from
/// `ConnectionState.connected` (design §3).
class ChatScreen extends StatelessWidget {
  const ChatScreen({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: appState,
      builder: (BuildContext context, Widget? _) {
        return SafeArea(
          child: Column(
            children: <Widget>[
              _NavBar(appState: appState),
              if (!appState.connected)
                _GuideCard(appState: appState)
              else ...<Widget>[
                _StatusCard(appState: appState),
                Expanded(child: _BubbleList(appState: appState)),
                _VoiceControls(appState: appState),
                _QuickPhrases(appState: appState),
                _ChatInputBar(appState: appState),
              ],
            ],
          ),
        );
      },
    );
  }
}

class _NavBar extends StatelessWidget {
  const _NavBar({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
      child: Row(
        children: <Widget>[
          const Expanded(
            child: Text(
              '对话',
              style: TextStyle(
                  fontSize: 34,
                  fontWeight: FontWeight.bold,
                  color: BanxiaTokens.label),
            ),
          ),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
            decoration: BoxDecoration(
              color: appState.connected
                  ? BanxiaTokens.green.withOpacity(0.15)
                  : BanxiaTokens.glass,
              borderRadius: BorderRadius.circular(999),
            ),
            child: Row(
              children: <Widget>[
                Icon(
                  Icons.circle,
                  size: 8,
                  color: appState.connected
                      ? BanxiaTokens.green
                      : BanxiaTokens.labelTertiary,
                ),
                const SizedBox(width: 6),
                Text(
                  appState.connected ? '已连接' : '未连接',
                  style: TextStyle(
                    fontSize: 12,
                    color: appState.connected
                        ? BanxiaTokens.green
                        : BanxiaTokens.labelSecondary,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _GuideCard extends StatelessWidget {
  const _GuideCard({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.all(20),
      padding: const EdgeInsets.all(28),
      decoration: BoxDecoration(
        color: BanxiaTokens.bgCard,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: const Color(0x14000000)),
      ),
      child: Column(
        children: <Widget>[
          const Text('还没绑定后端',
              style: TextStyle(
                  fontSize: 20,
                  fontWeight: FontWeight.bold,
                  color: BanxiaTokens.label)),
          const SizedBox(height: 8),
          const Text(
            '去设置 → 连接 输入服务器地址与 6 位配对码，即可开始对话',
            textAlign: TextAlign.center,
            style: TextStyle(fontSize: 15, color: BanxiaTokens.labelSecondary),
          ),
          const SizedBox(height: 20),
          _GlassButton(
            label: '去设置绑定',
            onTap: () => appState.switchTab(AppTab.settings),
          ),
        ],
      ),
    );
  }
}

class _StatusCard extends StatelessWidget {
  const _StatusCard({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.symmetric(horizontal: 20, vertical: 4),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: BanxiaTokens.bgCard,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: const Color(0x14000000)),
      ),
      child: Row(
        children: <Widget>[
          Container(
            width: 36,
            height: 36,
            decoration: const BoxDecoration(
              color: BanxiaTokens.tintFill,
              shape: BoxShape.circle,
            ),
            child: const Icon(Icons.person, color: Colors.white, size: 22),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: <Widget>[
                const Text('伴夏',
                    style: TextStyle(
                        fontWeight: FontWeight.bold,
                        fontSize: 17,
                        color: BanxiaTokens.label)),
                Text(
                  appState.conversation.transportStatus.isEmpty
                      ? appState.conversation.state
                      : '${appState.conversation.state} · ${appState.conversation.transportStatus}',
                  style: const TextStyle(
                      fontSize: 13, color: BanxiaTokens.labelSecondary),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _BubbleList extends StatelessWidget {
  const _BubbleList({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final bubbles = appState.conversation.bubbles;
    return ListView.builder(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
      itemCount: bubbles.length,
      itemBuilder: (BuildContext context, int index) {
        final ChatBubble bubble = bubbles[index];
        return Align(
          alignment:
              bubble.fromUser ? Alignment.centerRight : Alignment.centerLeft,
          child: Container(
            margin: const EdgeInsets.symmetric(vertical: 4),
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
            constraints: const BoxConstraints(maxWidth: 280),
            decoration: BoxDecoration(
              color:
                  bubble.fromUser ? BanxiaTokens.tintFill : BanxiaTokens.bgCard,
              borderRadius: BorderRadius.circular(20),
            ),
            child: Text(
              bubble.text,
              style: TextStyle(
                fontSize: 16,
                color: bubble.fromUser ? Colors.white : BanxiaTokens.label,
              ),
            ),
          ),
        );
      },
    );
  }
}

class _QuickPhrases extends StatelessWidget {
  const _QuickPhrases({required this.appState});

  final AppState appState;

  static const List<String> _phrases = <String>[
    '你好',
    '你是谁',
    '现在几点',
    '还记得我吗',
    '跳个舞',
    '链路测试',
  ];

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      height: 40,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: 20),
        children: <Widget>[
          for (final String phrase in _phrases)
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: GestureDetector(
                onTap: () => appState.dispatch(
                    Cmd.conversationSend, <String, dynamic>{'text': phrase}),
                child: Container(
                  alignment: Alignment.center,
                  padding: const EdgeInsets.symmetric(horizontal: 14),
                  decoration: BoxDecoration(
                    color: BanxiaTokens.glass,
                    borderRadius: BorderRadius.circular(999),
                  ),
                  child: Text(phrase,
                      style: const TextStyle(
                          fontSize: 14, color: BanxiaTokens.label)),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _VoiceControls extends StatelessWidget {
  const _VoiceControls({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final voice = appState.conversation;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 4),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Text(
              voice.recording
                  ? '正在录音'
                  : voice.monitoring
                      ? '正在聆听'
                      : '语音已关闭',
              style: const TextStyle(
                  fontSize: 13, color: BanxiaTokens.labelSecondary),
            ),
          ),
          IconButton(
            tooltip: '录音',
            onPressed: () => appState.dispatch(Cmd.voiceToggleRecord),
            icon: Icon(voice.recording ? Icons.stop : Icons.fiber_manual_record,
                color: voice.recording ? BanxiaTokens.red : BanxiaTokens.tint),
          ),
          IconButton(
            tooltip: '重启语音',
            onPressed: () => appState.dispatch(Cmd.voiceRestart),
            icon: const Icon(Icons.refresh, color: BanxiaTokens.tint),
          ),
          IconButton(
            tooltip: '取消语音',
            onPressed: () => appState.dispatch(Cmd.voiceCancel),
            icon: const Icon(Icons.close, color: BanxiaTokens.labelSecondary),
          ),
        ],
      ),
    );
  }
}

class _ChatInputBar extends StatefulWidget {
  const _ChatInputBar({required this.appState});

  final AppState appState;

  @override
  State<_ChatInputBar> createState() => _ChatInputBarState();
}

class _ChatInputBarState extends State<_ChatInputBar> {
  final TextEditingController _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _send() {
    final String text = _controller.text.trim();
    if (text.isEmpty) return;
    _controller.clear();
    widget.appState
        .dispatch(Cmd.conversationSend, <String, dynamic>{'text': text});
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 8, 20, 12),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Container(
              height: 46,
              padding: const EdgeInsets.symmetric(horizontal: 16),
              decoration: BoxDecoration(
                color: BanxiaTokens.glass,
                borderRadius: BorderRadius.circular(23),
              ),
              child: TextField(
                controller: _controller,
                style: const TextStyle(fontSize: 16, color: BanxiaTokens.label),
                decoration: const InputDecoration(
                  border: InputBorder.none,
                  hintText: '输入消息…',
                  hintStyle: TextStyle(
                      fontSize: 16, color: BanxiaTokens.labelSecondary),
                ),
                onSubmitted: (_) => _send(),
              ),
            ),
          ),
          const SizedBox(width: 8),
          GestureDetector(
            onTap: () => widget.appState.dispatch(Cmd.voiceToggleListen),
            child: Container(
              width: 46,
              height: 46,
              decoration: BoxDecoration(
                color: BanxiaTokens.glass,
                borderRadius: BorderRadius.circular(23),
              ),
              child: Icon(
                widget.appState.conversation.monitoring
                    ? Icons.mic
                    : Icons.mic_none,
                color: BanxiaTokens.tint,
                size: 22,
              ),
            ),
          ),
          const SizedBox(width: 8),
          GestureDetector(
            onTap: widget.appState.settings.camera
                ? () => widget.appState.dispatch(Cmd.conversationSendWithCamera)
                : () => widget.appState.showToast('请先在设置中开启摄像头单帧'),
            child: Container(
              width: 46,
              height: 46,
              decoration: BoxDecoration(
                color: widget.appState.settings.camera
                    ? BanxiaTokens.glass
                    : BanxiaTokens.glass.withOpacity(0.5),
                borderRadius: BorderRadius.circular(23),
              ),
              child: Icon(
                Icons.camera_alt_outlined,
                color: widget.appState.settings.camera
                    ? BanxiaTokens.tint
                    : BanxiaTokens.labelTertiary,
                size: 22,
              ),
            ),
          ),
          const SizedBox(width: 8),
          GestureDetector(
            onTap: _send,
            child: Container(
              width: 46,
              height: 46,
              decoration: const BoxDecoration(
                color: BanxiaTokens.tintFill,
                shape: BoxShape.circle,
              ),
              child:
                  const Icon(Icons.arrow_upward, color: Colors.white, size: 24),
            ),
          ),
        ],
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
          color: BanxiaTokens.tintFill,
          borderRadius: BorderRadius.circular(24),
        ),
        child: Text(label,
            style: const TextStyle(
                fontSize: 15,
                fontWeight: FontWeight.bold,
                color: Colors.white)),
      ),
    );
  }
}

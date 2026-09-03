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
          top: true,
          bottom: false,
          child: Column(
            children: <Widget>[
              _NavBar(appState: appState),
              if (!appState.connected)
                _GuideCard(appState: appState)
              else ...<Widget>[
                _StatusCard(appState: appState),
                Expanded(child: _BubbleList(appState: appState)),
                if (appState.conversation.suggestedReplies.isNotEmpty)
                  Flexible(
                    fit: FlexFit.loose,
                    child: _QuickPhrases(appState: appState),
                  ),
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
                const Text(
                  '伴夏',
                  style: TextStyle(
                    fontWeight: FontWeight.bold,
                    fontSize: 17,
                    color: BanxiaTokens.label,
                  ),
                ),
                Text(
                  appState.conversation.transportStatus.isEmpty
                      ? appState.conversation.state
                      : '${appState.conversation.state} · ${appState.conversation.transportStatus}',
                  style: const TextStyle(
                    fontSize: 13,
                    color: BanxiaTokens.labelSecondary,
                  ),
                ),
                Text(
                  appState.conversation.recording
                      ? '正在录音'
                      : appState.conversation.monitoring
                          ? '正在监听'
                          : '麦克风待命',
                  style: const TextStyle(
                    fontSize: 12,
                    color: BanxiaTokens.labelTertiary,
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

class _BubbleList extends StatefulWidget {
  const _BubbleList({required this.appState});

  final AppState appState;

  @override
  State<_BubbleList> createState() => _BubbleListState();
}

class _BubbleListState extends State<_BubbleList> {
  final ScrollController _scrollController = ScrollController();
  int _lastBubbleCount = 0;

  @override
  void dispose() {
    _scrollController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final bubbles = widget.appState.conversation.bubbles;
    if (bubbles.length != _lastBubbleCount) {
      _lastBubbleCount = bubbles.length;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && _scrollController.hasClients) {
          _scrollController.animateTo(
            _scrollController.position.maxScrollExtent,
            duration: const Duration(milliseconds: 160),
            curve: Curves.easeOut,
          );
        }
      });
    }
    return ListView.builder(
      controller: _scrollController,
      keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
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

  @override
  Widget build(BuildContext context) {
    final List<String> suggestions =
        appState.conversation.suggestedReplies.take(3).toList();
    return ConstrainedBox(
      constraints: const BoxConstraints(maxHeight: 174),
      child: ListView.builder(
        shrinkWrap: true,
        padding: const EdgeInsets.fromLTRB(20, 4, 20, 0),
        itemCount: suggestions.length,
        itemBuilder: (BuildContext context, int index) {
          return Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: GestureDetector(
              onTap: () => appState.dispatch(Cmd.conversationSend,
                  <String, dynamic>{'text': suggestions[index]}),
              child: Container(
                width: double.infinity,
                constraints: const BoxConstraints(minHeight: 38),
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                decoration: BoxDecoration(
                  color: BanxiaTokens.bgCard,
                  borderRadius: BorderRadius.circular(12),
                  border: Border.all(color: const Color(0x14000000)),
                ),
                child: Row(
                  children: <Widget>[
                    Container(
                      width: 24,
                      height: 24,
                      alignment: Alignment.center,
                      decoration: const BoxDecoration(
                        color: BanxiaTokens.tintFill,
                        shape: BoxShape.circle,
                      ),
                      child: Text('${index + 1}',
                          style: const TextStyle(
                              fontSize: 12, color: Colors.white)),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        suggestions[index],
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                            fontSize: 14, color: BanxiaTokens.label),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          );
        },
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

  void _sendWithCamera() {
    final String text = _controller.text.trim();
    _controller.clear();
    widget.appState.dispatch(
      Cmd.conversationSendWithCamera,
      <String, dynamic>{'text': text},
    );
  }

  @override
  Widget build(BuildContext context) {
    final bool cameraEnabled = widget.appState.settings.camera;
    final bool recording = widget.appState.conversation.recording;
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 8, 20, 12),
      child: Row(
        children: <Widget>[
          Expanded(
            child: Container(
              constraints: const BoxConstraints(minHeight: 46, maxHeight: 112),
              padding: const EdgeInsets.symmetric(horizontal: 16),
              decoration: BoxDecoration(
                color: BanxiaTokens.glass,
                borderRadius: BorderRadius.circular(23),
              ),
              child: TextField(
                controller: _controller,
                minLines: 1,
                maxLines: 4,
                textInputAction: TextInputAction.newline,
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
          const SizedBox(width: 6),
          SizedBox(
            width: 42,
            height: 46,
            child: PopupMenuButton<String>(
              tooltip: '语音控制',
              padding: EdgeInsets.zero,
              icon: Icon(
                recording ? Icons.mic : Icons.mic_none,
                color: recording
                    ? BanxiaTokens.red
                    : widget.appState.conversation.monitoring
                        ? BanxiaTokens.tint
                        : BanxiaTokens.labelSecondary,
                size: 23,
              ),
              onSelected: (String action) {
                switch (action) {
                  case 'listen':
                    widget.appState.dispatch(Cmd.voiceToggleListen);
                    return;
                  case 'record':
                    widget.appState.dispatch(Cmd.voiceToggleRecord);
                    return;
                  case 'restart':
                    widget.appState.dispatch(Cmd.voiceRestart);
                    return;
                  case 'cancel':
                    widget.appState.dispatch(Cmd.voiceCancel);
                    return;
                }
              },
              itemBuilder: (BuildContext context) => <PopupMenuEntry<String>>[
                PopupMenuItem<String>(
                  value: 'listen',
                  child: Text(widget.appState.conversation.monitoring
                      ? '关闭常开监听'
                      : '开启常开监听'),
                ),
                PopupMenuItem<String>(
                  value: 'record',
                  child: Text(recording ? '停止录音' : '开始录音'),
                ),
                const PopupMenuItem<String>(
                  value: 'restart',
                  child: Text('重启麦克风'),
                ),
                const PopupMenuItem<String>(
                  value: 'cancel',
                  child: Text('取消当前语音'),
                ),
              ],
            ),
          ),
          const SizedBox(width: 4),
          SizedBox(
            width: 42,
            height: 46,
            child: IconButton(
              tooltip: '拍摄并发送',
              padding: EdgeInsets.zero,
              onPressed: cameraEnabled ? _sendWithCamera : null,
              icon: Icon(
                Icons.camera_alt_outlined,
                color: cameraEnabled
                    ? BanxiaTokens.tint
                    : BanxiaTokens.labelTertiary,
                size: 22,
              ),
            ),
          ),
          const SizedBox(width: 4),
          SizedBox(
            width: 46,
            height: 46,
            child: IconButton(
              tooltip: '发送',
              padding: EdgeInsets.zero,
              onPressed: _send,
              style: IconButton.styleFrom(
                backgroundColor: BanxiaTokens.tintFill,
                foregroundColor: Colors.white,
                shape: const CircleBorder(),
              ),
              icon: const Icon(Icons.arrow_upward, size: 24),
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

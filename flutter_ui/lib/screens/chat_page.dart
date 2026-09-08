import 'package:flutter/material.dart';

import '../core/bridge/bridge_protocol.dart';
import '../main.dart';
import '../state/app_state.dart';

/// M6 微信式二级对话页（设计稿 docs/M6-flutter-chat-second-level-page.md）。
/// 由首页英雄卡 Navigator.push 进入；本路由内没有底部导航胶囊，
/// 输入框被悬浮导航遮挡的问题结构性消除。键盘避让只靠 Scaffold 的
/// resizeToAvoidBottomInset（严禁手写 viewInsets padding）。
class ChatPage extends StatelessWidget {
  const ChatPage({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: appState,
      builder: (BuildContext context, Widget? _) {
        return Scaffold(
          backgroundColor: BanxiaTokens.bg, // 不透明：RootShell 背景是透明的
          resizeToAvoidBottomInset: true,
          appBar: AppBar(
            backgroundColor: BanxiaTokens.bg,
            elevation: 0,
            scrolledUnderElevation: 0,
            leading: const BackButton(color: BanxiaTokens.tint),
            centerTitle: true,
            title: Column(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                const Text(
                  'ta',
                  style: TextStyle(
                    fontSize: 17,
                    fontWeight: FontWeight.bold,
                    color: BanxiaTokens.label,
                  ),
                ),
                _AppBarSubtitle(appState: appState),
              ],
            ),
          ),
          body: Stack(
            fit: StackFit.expand,
            children: <Widget>[
              Column(
                children: <Widget>[
                  if (!appState.connected)
                    _ConnectionBanner(appState: appState),
                  Expanded(child: _BubbleList(appState: appState)),
                  if (appState.conversation.suggestedReplies.isNotEmpty)
                    Flexible(
                      fit: FlexFit.loose,
                      child: _QuickPhrases(appState: appState),
                    ),
                  SafeArea(
                    top: false,
                    child: _ChatInputBar(appState: appState),
                  ),
                ],
              ),
              // 全局 _ToastOverlay 在 RootShell 内，会被本路由盖住；
              // 页内叠同款 toast 层（§3.7）。
              _ChatToastOverlay(appState: appState),
            ],
          ),
        );
      },
    );
  }
}

/// AppBar 副标题：吸收旧 _StatusCard 的三行状态为一行（§3.1）。
class _AppBarSubtitle extends StatelessWidget {
  const _AppBarSubtitle({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final conv = appState.conversation;
    final String status = conv.transportStatus.isEmpty
        ? conv.state
        : '${conv.state} · ${conv.transportStatus}';
    final Widget? micIcon = conv.recording
        ? const Padding(
            padding: EdgeInsets.only(right: 4),
            child: Icon(Icons.mic, size: 12, color: BanxiaTokens.red),
          )
        : conv.monitoring
            ? const Padding(
                padding: EdgeInsets.only(right: 4),
                child: Icon(Icons.hearing, size: 12, color: BanxiaTokens.tint),
              )
            : null;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Container(
          width: 6,
          height: 6,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: appState.connected
                ? BanxiaTokens.green
                : BanxiaTokens.labelTertiary,
          ),
        ),
        const SizedBox(width: 5),
        if (micIcon != null) micIcon,
        Flexible(
          child: Text(
            status,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(
              fontSize: 12,
              color: BanxiaTokens.labelSecondary,
            ),
          ),
        ),
      ],
    );
  }
}

/// 未连接横幅（替代整页 _GuideCard，§3.5）：消息流与输入条照常可用。
class _ConnectionBanner extends StatelessWidget {
  const _ConnectionBanner({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      color: BanxiaTokens.orange.withOpacity(0.12),
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 10),
      child: Row(
        children: <Widget>[
          const Icon(Icons.cloud_off, size: 18, color: BanxiaTokens.orange),
          const SizedBox(width: 8),
          const Expanded(
            child: Text(
              '未连接后端，消息无法送达',
              style: TextStyle(fontSize: 13, color: BanxiaTokens.label),
            ),
          ),
          GestureDetector(
            onTap: () {
              Navigator.of(context).pop();
              appState.switchTab(AppTab.settings);
            },
            child: const Text(
              '去绑定',
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.bold,
                color: BanxiaTokens.tint,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// 消息流（24 条气泡）：微信式不对称圆角 + >5 分钟时间头（§3.6）。
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

  static String _formatTime(DateTime at) {
    final String hh = at.hour.toString().padLeft(2, '0');
    final String mm = at.minute.toString().padLeft(2, '0');
    return '$hh:$mm';
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
    final double maxBubbleWidth = MediaQuery.sizeOf(context).width * 0.72;
    return ListView.builder(
      controller: _scrollController,
      keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
      itemCount: bubbles.length,
      itemBuilder: (BuildContext context, int index) {
        final ChatBubble bubble = bubbles[index];
        final bool showTimeHeader = index == 0 ||
            bubble.at.difference(bubbles[index - 1].at) >
                const Duration(minutes: 5);
        final BorderRadius radius = bubble.fromUser
            ? const BorderRadius.only(
                topLeft: Radius.circular(20),
                topRight: Radius.circular(20),
                bottomLeft: Radius.circular(20),
                bottomRight: Radius.circular(4),
              )
            : const BorderRadius.only(
                topLeft: Radius.circular(20),
                topRight: Radius.circular(20),
                bottomLeft: Radius.circular(4),
                bottomRight: Radius.circular(20),
              );
        return Column(
          children: <Widget>[
            if (showTimeHeader)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Text(
                  _formatTime(bubble.at),
                  style: const TextStyle(
                    fontSize: 11,
                    color: BanxiaTokens.labelTertiary,
                  ),
                ),
              ),
            Align(
              alignment: bubble.fromUser
                  ? Alignment.centerRight
                  : Alignment.centerLeft,
              child: Container(
                margin: const EdgeInsets.symmetric(vertical: 4),
                padding:
                    const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                constraints: BoxConstraints(maxWidth: maxBubbleWidth),
                decoration: BoxDecoration(
                  color: bubble.fromUser
                      ? BanxiaTokens.tintFill
                      : BanxiaTokens.bgCard,
                  borderRadius: radius,
                ),
                child: Text(
                  bubble.text,
                  style: TextStyle(
                    fontSize: 16,
                    color: bubble.fromUser ? Colors.white : BanxiaTokens.label,
                  ),
                ),
              ),
            ),
          ],
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

/// 页内 toast（样式与 RootShell._ToastOverlay 一致，§3.7）。
class _ChatToastOverlay extends StatelessWidget {
  const _ChatToastOverlay({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ValueListenableBuilder<ToastData?>(
      valueListenable: appState.toast,
      builder: (BuildContext context, ToastData? toast, Widget? _) {
        return IgnorePointer(
          child: AnimatedOpacity(
            opacity: toast == null ? 0 : 1,
            duration: const Duration(milliseconds: 160),
            child: Align(
              alignment: Alignment.bottomCenter,
              child: Container(
                margin: const EdgeInsets.only(bottom: 96, left: 40, right: 40),
                padding:
                    const EdgeInsets.symmetric(horizontal: 22, vertical: 12),
                decoration: BoxDecoration(
                  color: BanxiaTokens.glassChrome,
                  borderRadius: BorderRadius.circular(999),
                  border: Border.all(color: const Color(0x1F000000), width: 1),
                ),
                child: Text(
                  toast?.message ?? '',
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                    fontSize: 15,
                    color: BanxiaTokens.label,
                  ),
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}

using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Composes speech mouth shapes after imported VMD facial curves, while
    /// leaving the later physical-touch expression layer in control.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(11000)]
    public sealed class AvatarMouthLatePass : MonoBehaviour
    {
        private AvatarConversationPresenter presenter;

        internal void Initialize(AvatarConversationPresenter target)
        {
            presenter = target;
        }

        private void LateUpdate()
        {
            presenter?.ApplyMouthLatePass();
        }
    }
}

using System;
using System.Collections;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Figma
{
    internal static class Awaiters
    {
        public static WaitForEndOfFrame EndOfFrame { get; } = new();
        public static WaitForNextFrame NextFrame { get; } = new();
    }

    internal readonly struct WaitForNextFrame
    {
        public Awaiter GetAwaiter() => new();

        internal struct Awaiter : INotifyCompletion
        {
            public bool IsCompleted => false;
            public void GetResult() { }
            public void OnCompleted(Action continuation) => CoroutineRunner.Run(WaitOneFrame(continuation));
            static IEnumerator WaitOneFrame(Action continuation) { yield return null; continuation(); }
        }
    }

    internal static class WaitForEndOfFrameExtensions
    {
        public static Awaiter GetAwaiter(this WaitForEndOfFrame _) => new();

        internal struct Awaiter : INotifyCompletion
        {
            public bool IsCompleted => false;
            public void GetResult() { }
            public void OnCompleted(Action continuation) => CoroutineRunner.Run(WaitEndOfFrame(continuation));
            static IEnumerator WaitEndOfFrame(Action continuation) { yield return new WaitForEndOfFrame(); continuation(); }
        }
    }

    internal class CoroutineRunner : MonoBehaviour
    {
        static CoroutineRunner instance;

        static CoroutineRunner Instance
        {
            get
            {
                if (instance != null) return instance;
                var go = new GameObject("[CoroutineRunner]") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                instance = go.AddComponent<CoroutineRunner>();
                return instance;
            }
        }

        public static void Run(IEnumerator coroutine) => Instance.StartCoroutine(coroutine);
    }
}

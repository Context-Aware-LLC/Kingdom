using System;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Hot-seat / editor verification stub for <see cref="TurnClockView"/> (C-G1-2a). Because
    /// <c>LocalMatchSession</c> raises no <see cref="IMatchSession.OnTurnTimer"/>, the visible clock
    /// cannot be exercised by a real hot-seat match — so this driver stands in for the networked
    /// session's timer. On Play it builds a <see cref="TurnClockView"/> and fires
    /// <see cref="TurnClockView.OnTurnTimer"/> with a rolling deadline (default 75s per decision 19),
    /// re-arming at each expiry, so the countdown and the rope-window red tint are eyeballable
    /// without a server.
    ///
    /// Dev tool only — spawned via the "Emberfall/Verify Turn Clock (stub)" editor menu, never wired
    /// into a shipping bootstrap. This is E3's planned "fake session layer" applied to the one E2
    /// widget; it drives the exact method the networked seam drives, so what you see here is what a
    /// real <c>TurnTimerMsg</c> produces.
    /// </summary>
    public sealed class TurnClockStubDriver : MonoBehaviour
    {
        [SerializeField] private float turnSeconds = 75f;

        private TurnClockView _clock;
        private long _deadlineMs;

        private void Start()
        {
            var clockGo = new GameObject("TurnClock");
            clockGo.transform.SetParent(transform, false);
            _clock = clockGo.AddComponent<TurnClockView>();
            _clock.Init();
            Arm();
        }

        private void Update()
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _deadlineMs) Arm();
        }

        private void Arm()
        {
            _deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(turnSeconds * 1000f);
            // Same call the networked seam makes (NetworkedGameBootstrap wires _view.OnTurnTimer here).
            _clock.OnTurnTimer(new TurnTimer(new PlayerId("stub"), _deadlineMs));
        }
    }
}

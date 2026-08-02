// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Interfaces;

public interface IBayeuxPoller : IDisposable
{
    new void Dispose()
        {
            if (IsDestroyed)
                return;

            IsDestroyed = true;

            _notificationCts.Cancel();
            try { _notificationTask?.Wait(TimeSpan.FromSeconds(7)); }
            catch { }

            _notificationCts.Dispose();
        }
}

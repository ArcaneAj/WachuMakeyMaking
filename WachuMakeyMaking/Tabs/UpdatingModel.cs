using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace WachuMakeyMaking.Tabs
{
    public abstract class UpdatingModel
    {
        private volatile bool shouldUpdate;
        private readonly SemaphoreSlim updateLock = new(1, 1);
        private readonly Timer updateTimer;

        public UpdatingModel()
        {
            updateTimer = new Timer(8); // faster than 120 fps
            // Disable auto-repeat to prevent overlapping ticks, next tick will be started manually after work is done
            updateTimer.AutoReset = false;

            updateTimer.Elapsed += async (sender, e) =>
            {
                try
                {
                    if (!this.shouldUpdate)
                    {
                        return;
                    }

                    await updateLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (this.shouldUpdate)
                        {
                            this.shouldUpdate = false;

                            // Explicitly offload CPU-heavy work to a background ThreadPool thread
                            await Task.Run(UpdateAsync).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        updateLock.Release();
                    }
                }
                finally
                {
                    updateTimer.Start();
                }
            };

            updateTimer.Start();
        }

        protected abstract Task UpdateAsync();

        public void ScheduleUpdate()
        {
            this.shouldUpdate = true;
        }
    }
}

using System;
using System.Collections.Generic;

namespace WachuMakeyMaking.Events
{
    public class EventSource<T> : IEvent<T>
    {
        private readonly Dictionary<string, Action<T>> subscriptions = [];

        public IEvent<T> Event()
        {
            return this;
        }

        public void Emit(T val)
        {
            foreach (var subscription in this.subscriptions.Values)
            {
                subscription?.Invoke(val);
            }
        }

        public void Subscribe(string key, Action<T> action)
        {
            this.subscriptions[key] = action;
        }

        public void Unsubscribe(string key)
        {
            this.subscriptions.Remove(key);
        }
    }

    public class EventSource : IEvent
    {
        private readonly Dictionary<string, Action> subscriptions = [];

        public IEvent Event()
        {
            return this;
        }

        public void Emit()
        {
            foreach (var subscription in this.subscriptions.Values)
            {
                subscription?.Invoke();
            }
        }

        public void Subscribe(string key, Action action)
        {
            this.subscriptions[key] = action;
        }

        public void Unsubscribe(string key)
        {
            this.subscriptions.Remove(key);
        }
    }
}

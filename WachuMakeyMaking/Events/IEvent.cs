using System;

namespace WachuMakeyMaking.Events
{
    public interface IEvent<T>
    {
        public void Subscribe(string key, Action<T> action);
        public void Unsubscribe(string key);
    }

    public interface IEvent
    {
        public void Subscribe(string key, Action action);
        public void Unsubscribe(string key);
    }
}

using System;
using System.Collections.Generic;

namespace UpdateServer.Classes
{
    public class UpdateQueue<T>
    {
        private readonly Queue<T> queue = new Queue<T>();
        public int Count => queue.Count;

        public event EventHandler Changed;

        public virtual T Dequeue()
        {
            T item = queue.Dequeue();
            OnChanged();
            return item;
        }

        public virtual void Enqueue(T item)
        {
            queue.Enqueue(item);
            OnChanged();
        }

        protected virtual void OnChanged()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }
}
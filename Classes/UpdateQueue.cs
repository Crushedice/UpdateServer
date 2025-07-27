using System;
using System.Collections.Generic;

namespace UpdateServer.Classes
{
    public class UpdateQueue<T>
    {
        #region Fields
        private readonly Queue<T> queue = new Queue<T>();
        #endregion

        #region Properties
        public int Count => queue.Count;
        #endregion

        #region Events
        public event EventHandler Changed;
        #endregion

        #region Public Methods
        public virtual T Dequeue()
        {
            lock (queue)
            {
                T item = queue.Dequeue();
                OnChanged();
                return item;
            }
        }

        public virtual void Enqueue(T item)
        {
            lock (queue)
            {
                queue.Enqueue(item);
                OnChanged();
            }
        }
        #endregion

        #region Protected Methods
        protected virtual void OnChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        #endregion
    }
}
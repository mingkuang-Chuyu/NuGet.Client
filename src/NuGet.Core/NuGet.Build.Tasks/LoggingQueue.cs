using System;
using System.Threading.Tasks.Dataflow;

namespace NuGet.Build.Tasks
{
    internal abstract class LoggingQueue<T> : IDisposable
    {
        private readonly ActionBlock<T> _queue;

        protected LoggingQueue(int maxDegreeOfParallelism = 1)
        {
            _queue = new ActionBlock<T>(
                Process,
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism
                });
        }

        public bool Enqueue(T obj)
        {
            return _queue.Post(obj);
        }

        public void Dispose()
        {
            _queue.Complete();

            _queue.Completion.Wait();
        }

        protected abstract void Process(T item);
    }
}

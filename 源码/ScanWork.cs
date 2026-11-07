using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DiskGuide
{
    internal static class ScanWork
    {
        private static readonly SemaphoreSlim Gate=new SemaphoreSlim(1,1);
        public static async Task<T> Run<T>(Func<T> work,CancellationToken token)
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(()=>{
                    token.ThrowIfCancellationRequested();
                    var thread=Thread.CurrentThread;var previous=thread.Priority;
                    try{thread.Priority=ThreadPriority.BelowNormal;return work();}
                    finally{thread.Priority=previous;}
                },token).ConfigureAwait(false);
            }
            finally{Gate.Release();}
        }
    }
    internal sealed class ScanPacer
    {
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private int entries;
        public void Check(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(++entries%256==0 && clock.ElapsedMilliseconds>=20)
            {Thread.Sleep(1);clock.Restart();token.ThrowIfCancellationRequested();}
        }
    }
}

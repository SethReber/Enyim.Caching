//#define DEBUG_IO
using System;
using System.Net.Sockets;
using System.Threading;

namespace Enyim.Caching.Memcached
{
	public partial class PooledSocket
	{
		/// <summary>
		/// Supports exactly one reader and writer, but they can do IO concurrently
		/// </summary>
		class AsyncSocketHelper
		{
			const int ChunkSize = 65536;

			PooledSocket socket;
			SlidingBuffer asyncBuffer;

			SocketAsyncEventArgs readEvent;
			int remainingRead;
			int expectedToRead;
			AsyncIOArgs pendingArgs;

			int isAborted;
			ManualResetEvent readInProgressEvent;

			public AsyncSocketHelper(PooledSocket socket)
			{
				this.socket = socket;
				this.asyncBuffer = new SlidingBuffer(ChunkSize);

				this.readEvent = new SocketAsyncEventArgs();
				this.readEvent.Completed += new EventHandler<SocketAsyncEventArgs>(AsyncReadCompleted);
				this.readEvent.SetBuffer(new byte[ChunkSize], 0, ChunkSize);

				this.readInProgressEvent = new ManualResetEvent(false);
			}

			/// <summary>
			/// returns true if io is pending
			/// </summary>
			/// <param name="p"></param>
			/// <returns></returns>
			public bool Read(AsyncIOArgs p)
			{
				var count = p.Count;
				if (count < 1)
					throw new ArgumentOutOfRangeException("count", "count must be > 0");
				this.expectedToRead = p.Count;
				this.pendingArgs = p;

				p.Fail = false;
				p.Result = null;

				if (this.asyncBuffer.Available >= count)
				{
					PublishResult(false);

					return false;
				}
				else
				{
					this.remainingRead = count - this.asyncBuffer.Available;
					this.isAborted = 0;

					this.BeginReceive();

					return true;
				}
			}

			public void DiscardBuffer()
			{
				this.asyncBuffer.UnsafeClear();
			}

			void BeginReceive()
			{
				while (this.remainingRead > 0)
				{
					this.readInProgressEvent.Reset();

					if (this.socket._socket.ReceiveAsync(this.readEvent))
					{
						// wait until the timeout elapses, then abort this reading process
						// EndREceive will be triggered sooner or later but its timeout
						// may be higher than our read timeout, so it's not reliable
						if (!readInProgressEvent.WaitOne(this.socket._socket.ReceiveTimeout))
							this.AbortReadAndTryPublishError(false);

						return;
					}

					this.EndReceive();
				}
			}

			void AsyncReadCompleted(object sender, SocketAsyncEventArgs e)
			{
				if (this.EndReceive())
					this.BeginReceive();
			}

			void AbortReadAndTryPublishError(bool markAsDead)
			{
				if (markAsDead)
					this.socket._isAlive = false;

				// we've been already aborted, so quit
				// both the EndReceive and the wait on the event can abort the read
				// but only one should of them should continue the async call chain
				if (Interlocked.CompareExchange(ref this.isAborted, 1, 0) != 0)
					return;

				this.remainingRead = 0;
				var p = this.pendingArgs;

				p.Fail = true;
				p.Result = null;

				this.pendingArgs.Next(p);
			}

			/// <summary>
			/// returns true when io is pending
			/// </summary>
			/// <returns></returns>
			bool EndReceive()
			{
				this.readInProgressEvent.Set();

				var read = this.readEvent.BytesTransferred;
				if (this.readEvent.SocketError != SocketError.Success || read == 0)
				{
					this.AbortReadAndTryPublishError(true);

					return false;
				}

				this.remainingRead -= read;
				this.asyncBuffer.Append(this.readEvent.Buffer, 0, read);

				if (this.remainingRead <= 0)
				{
					this.PublishResult(true);

					return false;
				}

				return true;
			}

			void PublishResult(bool isAsync)
			{
				var retval = this.pendingArgs;

				var data = new byte[this.expectedToRead];
				this.asyncBuffer.Read(data, 0, retval.Count);
				pendingArgs.Result = data;

				if (isAsync)
					pendingArgs.Next(pendingArgs);
			}
		}
	}
}

#region [ License information          ]
/* ************************************************************
 * 
 *    � 2010 Attila Kisk� (aka Enyim), � 2016 CNBlogs, � 2017 VIEApps.net
 *    
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *    
 *        http://www.apache.org/licenses/LICENSE-2.0
 *    
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *    
 * ************************************************************/
#endregion

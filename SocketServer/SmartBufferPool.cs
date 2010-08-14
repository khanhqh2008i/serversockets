﻿// Copyright (C) 2010 OfficeSIP Communications
// This source is subject to the GNU General Public License.
// Please see Notice.txt for details.

using System;
using System.Collections.Generic;
using System.Threading;

namespace SocketServers
{
	/// <summary>
	/// Lock-free buffer pool implementation, it is more simple solution than classic memory
	/// manager algorithms (e.g. buddy memory manager). But it should use less extra calculation
	/// for allocation and freeing buffers. It like "smart" buffer pool, it slices desired sizes, 
	/// and do not free slices but put it in pool for further using. So big buffer should be sliced for
	/// concrete application.
	/// </summary>
	public class SmartBufferPool
	{
		public const int Kb = 1024;
		public const int Mb = Kb * Kb;
		public const int Gb = Mb * Kb;

		public readonly int MaxMemoryUsage;
		public readonly int InitialMemoryUsage;
		public readonly int ExtraMemoryUsage;
		public readonly int MaxBuffersCount;

		private const int MinSize = 1 * Kb;
		private const int MaxSize = 256 * Kb;

		private byte[][] buffers;
		private long indexOffset;
		private SafeStackItem<long>[] array;
		private SafeStack<long> empty;
		private SafeStack<long>[] ready;

		public SmartBufferPool(int maxMemoryUsageMb, int initialSizeMb, int extraBufferSizeMb)
		{
			InitialMemoryUsage = initialSizeMb * Mb;
			ExtraMemoryUsage = extraBufferSizeMb * Mb;
			MaxBuffersCount = (maxMemoryUsageMb * Mb - InitialMemoryUsage) / ExtraMemoryUsage;
			MaxMemoryUsage = InitialMemoryUsage + ExtraMemoryUsage * (MaxMemoryUsage - 1);

			array = new SafeStackItem<long>[MaxMemoryUsage / MinSize];

			empty = new SafeStack<long>(array, 0, array.Length);

			int count = 0;
			while (MaxSize >> count >= MinSize)
				count++;

			ready = new SafeStack<long>[count];
			for (int i = 0; i < ready.Length; i++)
				ready[i] = new SafeStack<long>(array, -1, -1);

			buffers = new byte[MaxBuffersCount][];
			buffers[0] = NewBuffer(InitialMemoryUsage);
		}

		public ArraySegment<byte> Allocate(int size)
		{
			if (size > MaxSize)
				throw new ArgumentOutOfRangeException("Too large size");

			size = MinSize << GetBitOffset(size);

			int offset, index;
			if (GetAllocated(size, out index, out offset) == false)
			{
				long copyIndexOffset;
				do
				{
					copyIndexOffset = Interlocked.Read(ref indexOffset);
					offset = (int)copyIndexOffset;
					index = (int)(copyIndexOffset >> 32);

					while (buffers[index] == null) Thread.Sleep(0);

					if ((buffers[index].Length - offset) < size)
					{
						if (index + 1 >= buffers.Length)
							throw new OutOfMemoryException("Source: BufferManager");

						if (Interlocked.CompareExchange(ref indexOffset, (long)(index + 1) << 32,
							copyIndexOffset) == copyIndexOffset)
						{
							buffers[index + 1] = NewBuffer(ExtraMemoryUsage);
						}

						continue;
					}

				} while (Interlocked.CompareExchange(ref indexOffset,
					copyIndexOffset + size, copyIndexOffset) != copyIndexOffset);
			}

			return new ArraySegment<byte>(buffers[index], offset, size);
		}

		public void Free(ArraySegment<byte> segment)
		{
			int bufferIndex = 0;
			while (buffers[bufferIndex] != segment.Array)
				bufferIndex++;

			int index = empty.Pop();
			array[index].Value = ((long)bufferIndex << 32) + segment.Offset;

			ready[GetBitOffset(segment.Count)].Push(index);
		}

		private bool GetAllocated(int size, out int index, out int offset)
		{
			int itemIndex = ready[GetBitOffset(size)].Pop();

			if (itemIndex >= 0)
			{
				index = (int)(array[itemIndex].Value >> 32);
				offset = (int)array[itemIndex].Value;

				empty.Push(itemIndex);

				return true;
			}
			else
			{
				index = -1;
				offset = -1;

				return false;
			}
		}

		private int GetBitOffset(int size)
		{
			int count = 0;
			while (size >> count > MinSize)
				count++;

			return count;
		}

		private static byte[] NewBuffer(int size)
		{
			var buffer = new byte[size];
			for (int i = 0; i < size; i += 1 * Kb)
				buffer[i] = 0;

			return buffer;
		}
	}
}

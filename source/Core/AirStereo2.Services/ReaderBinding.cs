using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace AirStereo2.Services;

public sealed class ReaderBinding
{
	private readonly record struct BufferedLine(string Text, int Utf8ByteCount);

	public const int MaxPreActiveQueueLines = 256;

	public const int MaxTotalBufferedBytes = 65536;

	public const int MaxLineLengthBytes = 4096;

	private int _status;

	private readonly object _bufferLock = new object();

	private Queue<BufferedLine>? _preActiveQueue = new Queue<BufferedLine>();

	private int _totalBufferedBytes;

	public Guid BindingId { get; }

	public Guid TransportInstanceId { get; }

	public ulong Generation { get; }

	public BindingToken Token => new BindingToken(BindingId, TransportInstanceId);

	internal Action? BeforeFlushDeliveryHookForTesting { get; set; }

	public ReaderBindingStatus Status => (ReaderBindingStatus)Volatile.Read(in _status);

	public bool IsActive => Status == ReaderBindingStatus.Active;

	public ReaderBinding(Guid transportInstanceId, ulong generation, Guid? bindingId = null)
	{
		BindingId = bindingId ?? Guid.NewGuid();
		TransportInstanceId = transportInstanceId;
		Generation = generation;
		_status = 0;
	}

	public void OnLineReceived(string rawLine, Action<ReaderBinding, string> deliverAction)
	{
		int byteCount = Encoding.UTF8.GetByteCount(rawLine);
		bool flag = false;
		bool flag2 = false;
		lock (_bufferLock)
		{
			switch (Status)
			{
			case ReaderBindingStatus.Active:
				flag = true;
				break;
			case ReaderBindingStatus.Starting:
				if (byteCount > 4096 || _preActiveQueue == null || _preActiveQueue.Count >= 256 || _totalBufferedBytes + byteCount > 65536)
				{
					Volatile.Write(ref _status, 2);
					_preActiveQueue?.Clear();
					_preActiveQueue = null;
					_totalBufferedBytes = 0;
					flag2 = true;
				}
				else
				{
					_preActiveQueue.Enqueue(new BufferedLine(rawLine, byteCount));
					_totalBufferedBytes += byteCount;
				}
				break;
			}
		}
		if (!flag2 && flag && IsActive)
		{
			deliverAction(this, rawLine);
		}
	}

	public void MarkActive(Action<ReaderBinding, string>? flushAction = null)
	{
		lock (_bufferLock)
		{
			if (Status != ReaderBindingStatus.Starting)
			{
				return;
			}
			Volatile.Write(ref _status, 1);
		}
		if (flushAction == null)
		{
			return;
		}
		try
		{
			while (true)
			{
				BufferedLine bufferedLine;
				lock (_bufferLock)
				{
					if (Status != ReaderBindingStatus.Active || _preActiveQueue == null || _preActiveQueue.Count == 0)
					{
						_preActiveQueue = null;
						_totalBufferedBytes = 0;
						break;
					}
					bufferedLine = _preActiveQueue.Dequeue();
					_totalBufferedBytes -= bufferedLine.Utf8ByteCount;
				}
				BeforeFlushDeliveryHookForTesting?.Invoke();
				flushAction(this, bufferedLine.Text);
			}
		}
		catch (Exception)
		{
			Cancel();
			throw;
		}
	}

	public void Cancel()
	{
		lock (_bufferLock)
		{
			Volatile.Write(ref _status, 2);
			_preActiveQueue?.Clear();
			_preActiveQueue = null;
			_totalBufferedBytes = 0;
		}
	}
}

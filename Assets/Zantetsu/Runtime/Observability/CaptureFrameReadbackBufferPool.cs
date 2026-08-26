using System;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity pool of persistent <see cref="NativeArray{T}"/> byte
    /// buffers intended for <c>AsyncGPUReadback</c>. Callers rent a slot, write
    /// into the slot's buffer, and return the slot when the GPU request has
    /// completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only; no locks or concurrent collections are used.
    /// </para>
    /// <para>
    /// A buffer must not be returned to the pool or the pool disposed until the
    /// associated GPU readback request has completed, otherwise the request may
    /// write into memory that has been handed to another slot or freed.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameReadbackBufferPool : IDisposable
    {
        private readonly NativeArray<byte>[] _buffers;
        private readonly int[] _freeIndices;
        private readonly bool[] _rented;
        private readonly int _bytesPerSlot;
        private int _freeCount;
        private bool _disposed;

        public CaptureFrameReadbackBufferPool(int slotCount, int bytesPerSlot)
        {
            if (slotCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount, "Slot count must be greater than zero.");
            }

            if (bytesPerSlot <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytesPerSlot), bytesPerSlot, "Bytes per slot must be greater than zero.");
            }

            _buffers = new NativeArray<byte>[slotCount];
            _freeIndices = new int[slotCount];
            _rented = new bool[slotCount];
            _bytesPerSlot = bytesPerSlot;
            _freeCount = slotCount;

            try
            {
                for (int i = 0; i < slotCount; i++)
                {
                    _buffers[i] = new NativeArray<byte>(bytesPerSlot, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    _freeIndices[i] = i;
                }
            }
            catch
            {
                for (int i = 0; i < slotCount; i++)
                {
                    if (_buffers[i].IsCreated)
                    {
                        _buffers[i].Dispose();
                    }
                }

                throw;
            }
        }

        public bool IsCreated => !_disposed;

        public int SlotCount
        {
            get
            {
                ThrowIfDisposed();
                return _buffers.Length;
            }
        }

        public int BytesPerSlot
        {
            get
            {
                ThrowIfDisposed();
                return _bytesPerSlot;
            }
        }

        public int AvailableCount
        {
            get
            {
                ThrowIfDisposed();
                return _freeCount;
            }
        }

        public int RentedCount
        {
            get
            {
                ThrowIfDisposed();
                return _buffers.Length - _freeCount;
            }
        }

        /// <summary>
        /// Rents a unique slot. Returns false with <paramref name="slotIndex"/>
        /// set to -1 when every slot is already rented.
        /// </summary>
        public bool TryRent(out int slotIndex)
        {
            ThrowIfDisposed();

            if (_freeCount == 0)
            {
                slotIndex = -1;
                return false;
            }

            _freeCount--;
            int index = _freeIndices[_freeCount];
            _rented[index] = true;
            slotIndex = index;
            return true;
        }

        /// <summary>
        /// Returns a non-owned view of the buffer for a currently rented slot.
        /// The caller must not dispose the returned
        /// <see cref="NativeArray{T}"/>.
        /// </summary>
        public NativeArray<byte> GetBuffer(int slotIndex)
        {
            ThrowIfDisposed();
            ValidateRentedSlot(slotIndex);
            return _buffers[slotIndex];
        }

        /// <summary>
        /// Returns a rented slot to the pool. The buffer contents are not
        /// cleared.
        /// </summary>
        public void Return(int slotIndex)
        {
            ThrowIfDisposed();
            ValidateRentedSlot(slotIndex);

            _rented[slotIndex] = false;
            _freeIndices[_freeCount] = slotIndex;
            _freeCount++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_freeCount != _buffers.Length)
            {
                throw new InvalidOperationException("Cannot dispose while slots are rented.");
            }

            _disposed = true;

            for (int i = 0; i < _buffers.Length; i++)
            {
                if (_buffers[i].IsCreated)
                {
                    _buffers[i].Dispose();
                }
            }
        }

        private void ValidateRentedSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _rented.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Slot index is out of range.");
            }

            if (!_rented[slotIndex])
            {
                throw new InvalidOperationException("Slot is not currently rented.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CaptureFrameReadbackBufferPool));
            }
        }
    }
}

using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameReadbackBufferPoolTests
    {
        private static readonly Type[] ForbiddenTypes =
        {
            typeof(RenderTexture),
            typeof(Camera),
            typeof(TraceLogger),
            typeof(UnityEngine.Logger),
            typeof(UnityEngine.Rendering.AsyncGPUReadbackRequest)
        };

        [Test]
        public void Constructor_InvalidArgs_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameReadbackBufferPool(0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameReadbackBufferPool(-1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameReadbackBufferPool(2, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameReadbackBufferPool(2, -1));
        }

        [Test]
        public void Buffers_CreatedWithSpecifiedLength()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(3, 64))
            {
                Assert.That(pool.SlotCount, Is.EqualTo(3));
                Assert.That(pool.BytesPerSlot, Is.EqualTo(64));

                for (int i = 0; i < 3; i++)
                {
                    Assert.That(pool.TryRent(out int slot), Is.True);
                    NativeArray<byte> buffer = pool.GetBuffer(slot);
                    Assert.That(buffer.IsCreated, Is.True);
                    Assert.That(buffer.Length, Is.EqualTo(64));
                    pool.Return(slot);
                }
            }
        }

        [Test]
        public void Rent_AllSlots_UniqueIndices()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(5, 8))
            {
                bool[] seen = new bool[5];
                int[] rented = new int[5];

                for (int i = 0; i < 5; i++)
                {
                    Assert.That(pool.TryRent(out int slot), Is.True);
                    Assert.That(slot, Is.InRange(0, 4));
                    Assert.That(seen[slot], Is.False);
                    seen[slot] = true;
                    rented[i] = slot;
                }

                foreach (int slot in rented)
                {
                    pool.Return(slot);
                }
            }
        }

        [Test]
        public void Rent_AllInUse_FalseAndMinusOne()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 8))
            {
                pool.TryRent(out int a);
                pool.TryRent(out int b);

                Assert.That(pool.TryRent(out int slot), Is.False);
                Assert.That(slot, Is.EqualTo(-1));
                Assert.That(pool.AvailableCount, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(2));

                pool.Return(a);
                pool.Return(b);
            }
        }

        [Test]
        public void RentReturn_Counts()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(3, 8))
            {
                Assert.That(pool.AvailableCount, Is.EqualTo(3));
                Assert.That(pool.RentedCount, Is.EqualTo(0));

                pool.TryRent(out int a);
                Assert.That(pool.AvailableCount, Is.EqualTo(2));
                Assert.That(pool.RentedCount, Is.EqualTo(1));

                pool.TryRent(out int b);
                Assert.That(pool.AvailableCount, Is.EqualTo(1));
                Assert.That(pool.RentedCount, Is.EqualTo(2));

                pool.Return(a);
                Assert.That(pool.AvailableCount, Is.EqualTo(2));
                Assert.That(pool.RentedCount, Is.EqualTo(1));

                pool.Return(b);
                Assert.That(pool.AvailableCount, Is.EqualTo(3));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Return_ThenRentableAgain()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 8))
            {
                pool.TryRent(out int a);
                pool.Return(a);

                Assert.That(pool.TryRent(out int b), Is.True);
                Assert.That(b, Is.EqualTo(a));
                Assert.That(pool.AvailableCount, Is.EqualTo(1));
                Assert.That(pool.RentedCount, Is.EqualTo(1));
                pool.Return(b);
            }
        }

        [Test]
        public void WriteByte_VisibleAcrossGetBufferCalls()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 16))
            {
                Assert.That(pool.TryRent(out int slot), Is.True);

                NativeArray<byte> buffer = pool.GetBuffer(slot);
                buffer[0] = 42;
                buffer[5] = 99;

                NativeArray<byte> again = pool.GetBuffer(slot);
                Assert.That(again[0], Is.EqualTo(42));
                Assert.That(again[5], Is.EqualTo(99));
                pool.Return(slot);
            }
        }

        [Test]
        public void Return_DoesNotClearContents()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 16))
            {
                pool.TryRent(out int slot);
                NativeArray<byte> buffer = pool.GetBuffer(slot);
                buffer[0] = 123;
                pool.Return(slot);

                Assert.That(pool.TryRent(out int slot2), Is.True);
                Assert.That(pool.GetBuffer(slot2)[0], Is.EqualTo(123));
                pool.Return(slot2);
            }
        }

        [Test]
        public void InvalidIndexAndState_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 8))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetBuffer(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetBuffer(2));
                Assert.Throws<ArgumentOutOfRangeException>(() => pool.Return(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => pool.Return(2));

                Assert.Throws<InvalidOperationException>(() => pool.GetBuffer(0));
                Assert.Throws<InvalidOperationException>(() => pool.Return(0));

                pool.TryRent(out int slot);
                pool.Return(slot);
                Assert.Throws<InvalidOperationException>(() => pool.Return(slot));
            }
        }

        [Test]
        public void Dispose_WithRentedSlot_FailsAndPoolUsable()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 8);
            pool.TryRent(out int slot);

            Assert.Throws<InvalidOperationException>(() => pool.Dispose());
            Assert.That(pool.IsCreated, Is.True);

            Assert.That(pool.TryRent(out int slot2), Is.True);
            pool.Return(slot);
            pool.Return(slot2);
            pool.Dispose();

            Assert.That(pool.IsCreated, Is.False);
        }

        [Test]
        public void Dispose_AllReturned_Succeeds()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 8);
            pool.TryRent(out int a);
            pool.TryRent(out int b);
            pool.Return(a);
            pool.Return(b);

            Assert.That(pool.IsCreated, Is.True);
            pool.Dispose();
            Assert.That(pool.IsCreated, Is.False);
        }

        [Test]
        public void Dispose_MultipleTimesSafe()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 8);
            pool.Dispose();

            Assert.DoesNotThrow(() => pool.Dispose());
        }

        [Test]
        public void Dispose_AllApiContract()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 8);
            pool.Dispose();

            Assert.That(pool.IsCreated, Is.False);
            Assert.Throws<ObjectDisposedException>(() => pool.TryRent(out _));
            Assert.Throws<ObjectDisposedException>(() => pool.GetBuffer(0));
            Assert.Throws<ObjectDisposedException>(() => pool.Return(0));
            Assert.Throws<ObjectDisposedException>(() => { int _ = pool.SlotCount; });
            Assert.Throws<ObjectDisposedException>(() => { int _ = pool.BytesPerSlot; });
            Assert.Throws<ObjectDisposedException>(() => { int _ = pool.AvailableCount; });
            Assert.Throws<ObjectDisposedException>(() => { int _ = pool.RentedCount; });
        }

        [Test]
        public void TwoPools_Independent()
        {
            using (CaptureFrameReadbackBufferPool p1 = new CaptureFrameReadbackBufferPool(1, 8))
            using (CaptureFrameReadbackBufferPool p2 = new CaptureFrameReadbackBufferPool(2, 16))
            {
                Assert.That(p1.SlotCount, Is.EqualTo(1));
                Assert.That(p2.SlotCount, Is.EqualTo(2));

                p1.TryRent(out int s1);
                Assert.That(p1.AvailableCount, Is.EqualTo(0));
                Assert.That(p2.AvailableCount, Is.EqualTo(2));

                NativeArray<byte> b1 = p1.GetBuffer(s1);
                b1[0] = 7;
                p2.TryRent(out int s2);
                NativeArray<byte> b2 = p2.GetBuffer(s2);
                b2[0] = 9;

                Assert.That(b1[0], Is.EqualTo(7));
                Assert.That(b2[0], Is.EqualTo(9));

                p1.Return(s1);
                p2.Return(s2);
            }
        }

        [Test]
        public void PublicApi_DoesNotExposeForbiddenTypes()
        {
            Type type = typeof(CaptureFrameReadbackBufferPool);

            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(Array.IndexOf(ForbiddenTypes, p.PropertyType), Is.EqualTo(-1), "Property exposes forbidden type: " + p.Name);
            }

            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(Array.IndexOf(ForbiddenTypes, m.ReturnType), Is.EqualTo(-1), "Method returns forbidden type: " + m.Name);

                foreach (ParameterInfo p in m.GetParameters())
                {
                    Assert.That(Array.IndexOf(ForbiddenTypes, p.ParameterType), Is.EqualTo(-1), "Method parameter exposes forbidden type: " + m.Name + "." + p.Name);
                }
            }

            foreach (ConstructorInfo c in type.GetConstructors())
            {
                foreach (ParameterInfo p in c.GetParameters())
                {
                    Assert.That(Array.IndexOf(ForbiddenTypes, p.ParameterType), Is.EqualTo(-1), "Constructor parameter exposes forbidden type: " + p.Name);
                }
            }
        }
    }
}

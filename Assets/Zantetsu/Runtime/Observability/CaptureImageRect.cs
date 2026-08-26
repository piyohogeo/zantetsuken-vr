using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Axis-aligned pixel rectangle for a captured frame. A value type with no
    /// reference-type fields.
    /// </summary>
    public readonly struct CaptureImageRect
    {
        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        public bool IsValid => X >= 0 && Y >= 0 && Width >= 1 && Height >= 1;

        public CaptureImageRect(int x, int y, int width, int height)
        {
            if (x < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(x), x, "X must not be negative.");
            }

            if (y < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(y), y, "Y must not be negative.");
            }

            if (width < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be at least 1.");
            }

            if (height < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be at least 1.");
            }

            if ((long)x + width > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "X + Width overflows Int32.");
            }

            if ((long)y + height > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Y + Height overflows Int32.");
            }

            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}

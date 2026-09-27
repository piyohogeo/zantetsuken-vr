using System;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The expected cost of the next <c>Physics.Simulate</c> (DESIGN 4.4: "the measured duration is used for the next
    /// cost prediction; the prediction method is an implementation detail"): the median of the last
    /// <see cref="Capacity"/> real simulations, or of as many as there have been. An even count takes the mean of the
    /// middle two. With none yet the prediction is 0, as the single last sample was before any simulation.
    /// <para>
    /// **Only a real simulation adds a sample.** A frame that did not simulate -- refused for its budget, paused, or
    /// owing less than a step -- changes nothing here, so refusing again and again never lowers the prediction.
    /// </para>
    /// <para>
    /// **What the median changes, and what it does not.** One or two long simulations among short ones no longer set the
    /// prediction on their own, so a single outlier cannot hold the steps back for good. A long simulation as the very
    /// first sample, or long ones making up most of the last five, still give a prediction that may never fit the
    /// budget, and then, with nothing simulated, nothing is ever added again: the steps can still stop for good. The
    /// median also follows a real, lasting rise in cost later than the last sample did -- only once most of the last
    /// five are long.
    /// </para>
    /// <para>
    /// Fixed storage: adding a sample and reading the median allocate nothing.
    /// </para>
    /// </summary>
    public sealed class SimulateCostHistory
    {
        /// <summary>How many of the latest real simulations the median is taken over.</summary>
        public const int Capacity = 5;

        private readonly double[] _samples = new double[Capacity];
        private readonly double[] _sorted = new double[Capacity];
        private int _count;
        private int _next;

        /// <summary>How many samples are held, at most <see cref="Capacity"/>.</summary>
        public int Count => _count;

        /// <summary>Forgets every sample: a play session starts with none.</summary>
        public void Clear()
        {
            _count = 0;
            _next = 0;
        }

        /// <summary>One real simulation's measured duration, in seconds. The oldest of five is dropped.</summary>
        public void Add(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "a measured duration is finite and not negative");
            }

            _samples[_next] = seconds;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }

        /// <summary>The expected cost of the next simulation: the median of the samples held, 0 with none.</summary>
        public double ExpectedSeconds
        {
            get
            {
                if (_count == 0)
                {
                    return 0.0;
                }

                // Insertion sort of at most five values into the fixed scratch.
                for (int i = 0; i < _count; i++)
                {
                    double value = _samples[i];
                    int j = i - 1;
                    while (j >= 0 && _sorted[j] > value)
                    {
                        _sorted[j + 1] = _sorted[j];
                        j--;
                    }

                    _sorted[j + 1] = value;
                }

                int middle = _count / 2;
                return (_count & 1) == 1 ? _sorted[middle] : 0.5 * (_sorted[middle - 1] + _sorted[middle]);
            }
        }
    }
}

using System;

namespace MonoTorrent
{
    static class IntMath
    {
        public static long PowUnchecked (long value, int power)
        {
            if (power < 0)
                throw new ArgumentOutOfRangeException (nameof (power), "Power must be greater than or equal to zero");
            if (power == 0)
                return 1;
            return (power & 1) == 0
                ? PowUnchecked (value * value, power >> 1)
                : value * PowUnchecked (value, power - 1);
        }

        public static long Pow (long value, int power)
        {
            if (power < 0)
                throw new ArgumentOutOfRangeException (nameof (power), "Power must be greater than or equal to zero");
            if (power == 0)
                return 1;
            return checked((power & 1) == 0
                ? Pow (value * value, power >> 1)
                : value * Pow (value, power - 1));
        }

        public static int Pow (int value, int power)
        {
            if (power < 0)
                throw new ArgumentOutOfRangeException (nameof (power), "Power must be greater than or equal to zero");
            if (power == 0)
                return 1;
            return checked((power & 1) == 0
                ? Pow (value * value, power >> 1)
                : value * Pow (value, power - 1));
        }
    }
}

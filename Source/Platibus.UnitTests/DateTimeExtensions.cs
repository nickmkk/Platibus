﻿using System;

namespace Platibus.UnitTests
{
    internal static class DateTimeExtensions
    {
        public static DateTime TruncateMillis(this DateTime dt)
        {
            return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Kind);
        }
    }
}

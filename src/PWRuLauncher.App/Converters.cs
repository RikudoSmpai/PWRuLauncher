using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PWRuLauncher
{
    public static class Converters
    {
        /// <summary>Строка-раскрытие пути: 38 px свёрнута, 30 px раскрыта.</summary>
        public static readonly IValueConverter OpenToSummaryHeight =
            new FuncValueConverter<bool, double>(open => open ? 30d : 38d);
    }
}
